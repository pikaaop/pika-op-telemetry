"""Local debug telemetry overlay and explicitly enabled camera tracking."""
import ctypes
from ctypes import wintypes
import json
import math
from pathlib import Path
import struct
import time
from telemetry import DEFAULT, project
from dump_tools import resolve_dump_folder, inspect_dump
from viewer_tools import read_settings, write_settings, filtered_players, export_csv, Recorder


def resolve_entity(pointer, root, index, stride, table=0x10):
    if not 0 <= index <= 0x7FFF:
        return 0
    chunk = pointer(root + table + 8 * (index >> 9))
    return pointer(chunk + stride * (index & 0x1FF)) if chunk else 0


def validate_layout(pointer, read_uint, root, controller, pawn, handle_offset):
    """Accept a candidate only when both local anchors match exactly."""
    handle = read_uint(controller + handle_offset)
    if handle in (0, 0xFFFFFFFF):
        raise ValueError('Local controller has no pawn; enter a local map')
    for stride in (0x70, 0x78):
        for slot in range(1, 65):
            try:
                if (resolve_entity(pointer, root, slot, stride) == controller and
                    resolve_entity(pointer, root, handle & 0x7FFF, stride) == pawn):
                    return stride
            except (OSError, ValueError):
                continue
    raise ValueError('Entity layout does not match local anchors; dump may be stale')


def choose_target(players, center, radius, previous=None):
    candidates = [(math.dist(p['aim'], center), p) for p in players
                  if p.get('aim') is not None]
    candidates = [(distance, p) for distance, p in candidates if distance <= radius]
    locked = next((p for _, p in candidates if p['address'] == previous), None)
    return locked or (min(candidates, key=lambda pair: pair[0])[1] if candidates else None)


def tracking_step(point, center, dt, tau, gain, residual=(0, 0), deadzone=1.5):
    if math.dist(point, center) <= deadzone:
        return (0, 0), (0, 0)
    dt = max(0, min(dt, .05))
    alpha = -math.expm1(-dt / tau)
    movement = [(p-c)*alpha*gain+r for p,c,r in zip(point,center,residual)]
    limit = 800 * dt
    counts = tuple(round(max(-limit, min(limit, v))) for v in movement)
    remainder = tuple(v-n if abs(v) <= limit else 0 for v,n in zip(movement,counts))
    return counts, remainder


def calibrated_step(point, center, matrix, sensitivity, scope_scale, dt, tau,
                    multiplier=1.0, residual=(0,0)):
    """Rectilinear projection, orthonormal view, default m_yaw/m_pitch=0.022.

    The multiplier calibrates nondefault input settings. SendInput response
    must be verified on the actual debug session.
    """
    if not (.01 <= sensitivity <= 100 and .01 <= scope_scale <= 4):
        raise ValueError('Invalid mouse sensitivity; tracking stopped')
    if center[0] <= 0 or center[1] <= 0:
        raise ValueError('Invalid viewport')
    if math.dist(point,center) < .5:
        return (0,0),(0,0)
    scale = math.hypot(*matrix[12:15])
    fx,fy = math.hypot(*matrix[:3]),math.hypot(*matrix[4:7])
    if min(scale,fx,fy) < 1e-6:
        raise ValueError('View matrix cannot determine projection scale')
    x = (point[0]-center[0])/center[0]*scale/fx
    y = (point[1]-center[1])/center[1]*scale/fy
    degrees_per_count = .022*sensitivity*scope_scale
    counts = (math.degrees(math.atan(x))/degrees_per_count,
              math.degrees(math.atan2(y,math.hypot(x,1)))/degrees_per_count)
    return tracking_step(counts,(0,0),dt,tau,multiplier,residual,deadzone=0)


def load_skeleton_profile(path):
    if not path.exists():
        return None
    profile = json.loads(path.read_text())
    for key in ('pointer_offset', 'stride', 'position_offset'):
        if type(profile.get(key)) is not int or not 0 <= profile[key] <= 65536:
            raise ValueError(f'Invalid skeleton {key}')
    if not 12 <= profile['stride'] <= 256 or profile['position_offset']+12 > profile['stride']:
        raise ValueError('Invalid bone structure size')
    joints = profile.get('joints', {})
    if not joints or any(type(i) is not int or not 0 <= i < 256 for i in joints.values()):
        raise ValueError('Skeleton requires verified joint indices')
    for edge in profile.get('edges', []):
        if len(edge) != 2 or any(name not in joints for name in edge):
            raise ValueError('Unknown joint in skeleton edge')
    return profile


def projected_bounds(origin, minimum, maximum, matrix, width, height):
    points = [project(tuple(origin[i]+corner[i] for i in range(3)), matrix, width, height,False)
              for x in (minimum[0],maximum[0])
              for y in (minimum[1],maximum[1])
              for z in (minimum[2],maximum[2]) for corner in [(x,y,z)]]
    if any(p is None for p in points):
        return None
    left,top,right,bottom = (min(p[0] for p in points),min(p[1] for p in points),
                            max(p[0] for p in points),max(p[1] for p in points))
    if right < 0 or bottom < 0 or left > width or top > height:
        return None
    return max(0,left),max(0,top),min(width,right),min(height,bottom)


class Reader:
    def __init__(self, folder=DEFAULT):
        import pymem
        import pymem.process
        from pymem.exception import MemoryReadError
        self.read_errors = (MemoryReadError, ValueError, OSError)
        self.folder = resolve_dump_folder(folder)
        self.all_offsets = json.loads((self.folder/'offsets.json').read_text())
        self.offsets = self.all_offsets['client.dll']
        self.classes = json.loads((self.folder/'client_dll.json').read_text())['client.dll']['classes']
        info_file = self.folder/'info.json'
        self.info = json.loads(info_file.read_text()) if info_file.exists() else {}
        self.skeleton = load_skeleton_profile(Path(__file__).with_name('skeleton_profile.json'))
        self.pm = pymem.Pymem('cs2.exe')
        try:
            module = pymem.process.module_from_name(self.pm.process_handle, 'client.dll')
            if module is None:
                raise ValueError('client.dll is not loaded')
            self.base = module.lpBaseOfDll
            self.stride = None
            self.build = None
            engine = pymem.process.module_from_name(self.pm.process_handle,'engine2.dll')
            if engine and 'dwBuildNumber' in self.all_offsets.get('engine2.dll',{}):
                self.build = self.read(engine.lpBaseOfDll+self.all_offsets['engine2.dll']['dwBuildNumber'],'<I')[0]
                expected = self.info.get('build_number')
                if expected and self.build != expected:
                    raise ValueError(f'Dump build {expected} does not match running build {self.build}; regenerate dump')
        except Exception:
            self.pm.close_process()
            raise

    def field(self, cls, name):
        return self.classes[cls]['fields'][name]

    def read(self, address, format):
        return struct.unpack(format, self.pm.read_bytes(address, struct.calcsize(format)))

    def pointer(self, address):
        try:
            return self.read(address, '<Q')[0] if address else 0
        except self.read_errors:
            return 0

    def uint(self, address):
        return self.read(address, '<I')[0]

    def global_pointer(self, name):
        return self.pointer(self.base + self.offsets[name])

    def string(self, pointer, fallback=''):
        if not pointer:
            return fallback
        raw = bytearray()
        # Read small pieces so short strings near page boundaries remain readable.
        for offset in range(0,128,8):
            piece = self.pm.read_bytes(pointer+offset,8)
            raw.extend(piece.split(b'\0',1)[0])
            if b'\0' in piece:
                break
        text = raw.decode('utf-8',errors='replace')
        return ''.join(c for c in text if c.isprintable())[:64] or fallback

    def weapon(self, pawn, root):
        services = self.pointer(pawn+self.field('C_BasePlayerPawn','m_pWeaponServices'))
        if not services:
            return dict(name='Unknown',ammo=None,item_id=None)
        handle = self.uint(services+self.field('CPlayer_WeaponServices','m_hActiveWeapon'))
        entity = resolve_entity(self.pointer,root,handle & 0x7FFF,self.stride) if handle != 0xFFFFFFFF else 0
        if not entity:
            return dict(name='None',ammo=None,item_id=None)
        identity = self.pointer(entity+self.field('CEntityInstance','m_pEntity'))
        name_pointer = self.pointer(identity+self.field('CEntityIdentity','m_designerName')) if identity else 0
        name = self.string(name_pointer,'Unknown').removeprefix('weapon_')
        ammo = self.read(entity+self.field('C_BasePlayerWeapon','m_iClip1'),'<i')[0]
        if not -1 <= ammo <= 1000:
            ammo = None
        item = (entity+self.field('C_EconEntity','m_AttributeManager')+
                self.field('C_AttributeContainer','m_Item')+
                self.field('C_EconItemView','m_iItemDefinitionIndex'))
        return dict(name=name,ammo=ammo,item_id=self.read(item,'<H')[0])

    def extra_player_data(self, pawn, controller, root, slot):
        result = dict(name=f'Player {slot}',armor=None,scoped=None,shots=None,
                      weapon=dict(name='Unknown',ammo=None,item_id=None))
        reads = {
            'name':lambda:self.string(self.pointer(controller+self.field('CCSPlayerController','m_sSanitizedPlayerName')),f'Player {slot}'),
            'armor':lambda:self.read(pawn+self.field('C_CSPlayerPawn','m_ArmorValue'),'<i')[0],
            'scoped':lambda:bool(self.read(pawn+self.field('C_CSPlayerPawn','m_bIsScoped'),'<B')[0]),
            'shots':lambda:self.read(pawn+self.field('C_CSPlayerPawn','m_iShotsFired'),'<i')[0],
            'weapon':lambda:self.weapon(pawn,root),
        }
        for key,read in reads.items():
            try:
                result[key] = read()
            except self.read_errors:
                pass  # Optional telemetry must not remove an otherwise valid player.
        if result['armor'] is not None and not 0 <= result['armor'] <= 1000:
            result['armor'] = None
        if result['shots'] is not None and not 0 <= result['shots'] <= 1000:
            result['shots'] = None
        return result

    def position(self, entity):
        node = self.pointer(entity + self.field('C_BaseEntity','m_pGameSceneNode'))
        if not node:
            raise ValueError('Missing scene node')
        value = self.read(node + self.field('CGameSceneNode','m_vecAbsOrigin'), '<3f')
        if not all(math.isfinite(v) and abs(v) < 1e7 for v in value):
            raise ValueError('Invalid position')
        return value

    def bones(self, entity, origin):
        if self.skeleton is None:
            return {}
        profile = self.skeleton
        scene = self.pointer(entity+self.field('C_BaseEntity','m_pGameSceneNode'))
        model_state = scene+self.field('CSkeletonInstance','m_modelState')
        buffer = self.pointer(model_state+profile['pointer_offset'])
        if not buffer:
            return {}
        length = max(profile['joints'].values())*profile['stride']+profile['position_offset']+12
        raw = self.pm.read_bytes(buffer,length)
        joints = {name:struct.unpack_from('<3f',raw,index*profile['stride']+profile['position_offset'])
                  for name,index in profile['joints'].items()}
        if any(not all(map(math.isfinite,p)) or math.dist(p,origin)>256 for p in joints.values()):
            return {}
        if any(not .1 < math.dist(joints[a],joints[b]) < 128 for a,b in profile.get('edges', [])):
            return {}
        return joints

    def frame(self):
        local = self.global_pointer('dwLocalPlayerPawn')
        controller = self.global_pointer('dwLocalPlayerController')
        root = self.global_pointer('dwEntityList')
        if not all((local, controller, root)):
            self.stride = None
            raise ValueError('Waiting for a local player; enter a map')
        handle_offset = self.field('CCSPlayerController','m_hPlayerPawn')
        if self.stride is None:
            self.stride = validate_layout(self.pointer, self.uint, root, controller,
                                          local, handle_offset)
        # Recheck anchor every frame; reject a changed container instead of guessing.
        handle = self.uint(controller + handle_offset)
        if resolve_entity(self.pointer, root, handle & 0x7FFF, self.stride) != local:
            self.stride = None
            raise ValueError('Local entity changed; validating again')
        origin = self.position(local)
        self.local = local
        self.local_data = self.extra_player_data(local,controller,root,0)
        team = self.read(local + self.field('C_BaseEntity','m_iTeamNum'), '<B')[0]
        matrix = self.read(self.base + self.offsets['dwViewMatrix'], '<16f')
        if not all(map(math.isfinite,matrix)):
            raise ValueError('Invalid view matrix')
        players, seen = [], set()
        for slot in range(1,65):
            try:
                player_controller = resolve_entity(self.pointer,root,slot,self.stride)
                if not player_controller:
                    continue
                handle = self.uint(player_controller + handle_offset)
                if handle in (0,0xFFFFFFFF):
                    continue
                pawn = resolve_entity(self.pointer,root,handle & 0x7FFF,self.stride)
                if not pawn or pawn == local or pawn in seen:
                    continue
                seen.add(pawn)
                health = self.read(pawn+self.field('C_BaseEntity','m_iHealth'),'<i')[0]
                life = self.read(pawn+self.field('C_BaseEntity','m_lifeState'),'<B')[0]
                other_team = self.read(pawn+self.field('C_BaseEntity','m_iTeamNum'),'<B')[0]
                if life or not 0 < health <= 1000 or other_team not in (2,3):
                    continue
                position = self.position(pawn)
                minimum,maximum = (-16,-16,0),(16,16,64)
                bounds_valid = False
                collision = self.pointer(pawn+self.field('C_BaseEntity','m_pCollision'))
                if collision:
                    low = self.read(collision+self.field('CCollisionProperty','m_vecMins'),'<3f')
                    high = self.read(collision+self.field('CCollisionProperty','m_vecMaxs'),'<3f')
                    if all(math.isfinite(a) and math.isfinite(b) and 0 < b-a < 256
                           for a,b in zip(low,high)):
                        minimum,maximum = low,high
                        bounds_valid = True
                try:
                    bones = self.bones(pawn,position)
                except self.read_errors:
                    bones = {}
                players.append(dict(address=pawn,health=health,team=other_team,
                                    position=position,slot=slot,minimum=minimum,
                                    maximum=maximum,bones=bones,bounds_valid=bounds_valid,
                                    **self.extra_player_data(pawn,player_controller,root,slot)))
            except self.read_errors:
                continue  # An object can disappear while traversing live memory.
        return origin,team,matrix,players

    def input_calibration(self):
        address = self.global_pointer('dwSensitivity')
        if not address:
            raise ValueError('Mouse sensitivity unavailable')
        sensitivity = self.read(address+self.offsets['dwSensitivity_sensitivity'],'<f')[0]
        scale = self.read(self.local+self.field('C_BasePlayerPawn','m_flFOVSensitivityAdjust'),'<f')[0]
        scoped = bool(self.read(self.local+self.field('C_CSPlayerPawn','m_bIsScoped'),'<B')[0])
        if not math.isfinite(sensitivity) or not math.isfinite(scale):
            raise ValueError('Invalid input calibration')
        return sensitivity,scale,scoped

    def close(self):
        self.pm.close_process()


def make_mouse_sender():
    class MouseInput(ctypes.Structure):
        _fields_ = [('dx',wintypes.LONG),('dy',wintypes.LONG),
                    ('mouseData',wintypes.DWORD),('dwFlags',wintypes.DWORD),
                    ('time',wintypes.DWORD),('dwExtraInfo',ctypes.c_size_t)]
    class KeyboardInput(ctypes.Structure):
        _fields_ = [('wVk',wintypes.WORD),('wScan',wintypes.WORD),
                    ('dwFlags',wintypes.DWORD),('time',wintypes.DWORD),
                    ('dwExtraInfo',ctypes.c_size_t)]
    class HardwareInput(ctypes.Structure):
        _fields_ = [('uMsg',wintypes.DWORD),('wParamL',wintypes.WORD),('wParamH',wintypes.WORD)]
    class Payload(ctypes.Union):
        _fields_ = [('mi',MouseInput),('ki',KeyboardInput),('hi',HardwareInput)]
    class Input(ctypes.Structure):
        _fields_ = [('type',wintypes.DWORD),('payload',Payload)]
    user32 = ctypes.WinDLL('user32',use_last_error=True)
    user32.SendInput.argtypes = (wintypes.UINT,ctypes.POINTER(Input),ctypes.c_int)
    user32.SendInput.restype = wintypes.UINT
    def send(dx,dy):
        if not dx and not dy:
            return
        event = Input(type=0,payload=Payload(mi=MouseInput(dx,dy,0,1,0,0)))
        if user32.SendInput(1,ctypes.byref(event),ctypes.sizeof(Input)) != 1:
            raise OSError('SendInput failed; inspect process integrity levels')
    return send


def main():
    import tkinter as tk
    from tkinter import ttk, filedialog
    from datetime import datetime, timezone
    import win32gui as wg
    import win32api as wa
    import win32con as wc
    import win32process as wp
    ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))

    root = tk.Tk()
    root.title('pika op')
    screen_w,screen_h = root.winfo_screenwidth(),root.winfo_screenheight()
    root.geometry(f'{min(1120,screen_w-60)}x{min(820,screen_h-80)}')
    root.minsize(min(960,screen_w-60),min(650,screen_h-80))
    root.configure(bg='#111827')
    style = ttk.Style(root)
    style.theme_use('clam')
    style.configure('.',background='#111827',foreground='#e5e7eb',font=('Segoe UI',10))
    style.configure('TButton',background='#263449',padding=8)
    style.map('TButton',background=[('active','#344862')])
    style.configure('TCheckbutton',background='#111827')
    style.configure('TNotebook',background='#111827',borderwidth=0)
    style.configure('TNotebook.Tab',padding=(18,10),background='#1f2937')
    style.map('TNotebook.Tab',background=[('selected','#334155')])
    style.layout('Hidden.TNotebook.Tab',[])
    style.configure('Sidebar.TFrame',background='#0b1120')
    style.configure('Sidebar.TLabel',background='#0b1120',foreground='#94a3b8')
    style.configure('Nav.TButton',background='#0b1120',foreground='#94a3b8',anchor='w',padding=(16,12),borderwidth=0)
    style.configure('Active.Nav.TButton',background='#17382f',foreground='#6ee7b7',anchor='w',padding=(16,12),borderwidth=0)
    style.map('Nav.TButton',background=[('active','#1f2937')])
    style.configure('Treeview',background='#0f172a',fieldbackground='#0f172a',foreground='#e5e7eb',rowheight=30,borderwidth=0)
    style.configure('Treeview.Heading',background='#1f2937',foreground='#94a3b8',padding=8)
    style.map('Treeview',background=[('selected','#174c42')])
    sidebar = ttk.Frame(root,style='Sidebar.TFrame',width=190,padding=(12,22))
    sidebar.pack(side='left',fill='y')
    sidebar.pack_propagate(False)
    ttk.Label(sidebar,text='pika op',font=('Segoe UI',24,'bold'),style='Sidebar.TLabel',foreground='#6ee7b7').pack(anchor='w',padx=12)
    ttk.Label(sidebar,text='LOCAL RESEARCH',font=('Segoe UI',9),style='Sidebar.TLabel').pack(anchor='w',padx=14,pady=(2,24))
    panel = ttk.Frame(root,padding=22)
    panel.pack(fill='both',expand=True)
    page_title = tk.StringVar(value='Overview')
    ttk.Label(panel,textvariable=page_title,font=('Segoe UI',25,'bold')).pack(anchor='w')
    ttk.Label(panel,text='Live telemetry · local session',foreground='#94a3b8').pack(anchor='w',pady=(0,18))
    esp = tk.BooleanVar(value=True)
    aim = tk.BooleanVar(value=False)
    teammates = tk.BooleanVar(value=False)
    aim_teammates = tk.BooleanVar(value=False)
    skeleton = tk.BooleanVar(value=True)
    show_distance = tk.BooleanVar(value=True)
    show_radius = tk.BooleanVar(value=True)
    preaim = tk.BooleanVar(value=True)
    corners = tk.BooleanVar(value=True)
    show_names = tk.BooleanVar(value=True)
    show_weapons = tk.BooleanVar(value=True)
    show_armor = tk.BooleanVar(value=True)
    snaplines = tk.BooleanVar(value=False)
    automatic_preset = tk.BooleanVar(value=False)
    paused = tk.BooleanVar(value=False)
    crosshair = tk.BooleanVar(value=False)
    max_distance = tk.DoubleVar(value=0)
    overlay_alpha = tk.DoubleVar(value=1.0)
    label_size = tk.DoubleVar(value=9.0)
    line_width = tk.DoubleVar(value=2.0)
    radar_range = tk.DoubleVar(value=2000.0)
    palette = tk.StringVar(value='Emerald')
    query = tk.StringVar(value='')
    recording_status = tk.StringVar(value='Not recording')
    connection = tk.StringVar(value='DISCONNECTED')
    source_folder = tk.StringVar(value=str(DEFAULT))
    preset = tk.StringVar(value='Rifle')
    aim_joint = tk.StringVar(value='Chest')
    radius = tk.DoubleVar(value=180)
    tau = tk.DoubleVar(value=.18)
    gain = tk.DoubleVar(value=1.0)
    height = tk.DoubleVar(value=64)
    status = tk.StringVar(value='Waiting to connect')
    details = tk.StringVar(value='')
    tabs = ttk.Notebook(panel,style='Hidden.TNotebook')
    tabs.pack(fill='both',expand=True)
    page_names = ('Overview','Visuals','Tracking','Players','Recording','Practice','Status')
    pages,scroll_canvases,nav_buttons = [],[],[]
    for index,title in enumerate(page_names):
        outer = ttk.Frame(tabs)
        scroll = tk.Canvas(outer,bg='#111827',highlightthickness=0)
        scrollbar = ttk.Scrollbar(outer,orient='vertical',command=scroll.yview)
        scroll.configure(yscrollcommand=scrollbar.set)
        scrollbar.pack(side='right',fill='y')
        scroll.pack(side='left',fill='both',expand=True)
        inner = ttk.Frame(scroll,padding=(4,10,18,16))
        item = scroll.create_window((0,0),window=inner,anchor='nw')
        inner.bind('<Configure>',lambda event,scroll=scroll:scroll.configure(scrollregion=scroll.bbox('all')))
        scroll.bind('<Configure>',lambda event,scroll=scroll,item=item:scroll.itemconfigure(item,width=event.width))
        tabs.add(outer,text=title)
        pages.append(inner)
        scroll_canvases.append(scroll)
        button = ttk.Button(sidebar,text=title,style='Nav.TButton',command=lambda index=index:tabs.select(index))
        button.pack(fill='x',pady=3)
        nav_buttons.append(button)
    overview,visuals,tracking,players_tab,recording,practice,diagnostics = pages
    def page_changed(unused=None):
        index = tabs.index('current')
        page_title.set(page_names[index])
        for i,button in enumerate(nav_buttons):
            button.configure(style='Active.Nav.TButton' if i==index else 'Nav.TButton')
    tabs.bind('<<NotebookTabChanged>>',page_changed)
    page_changed()
    def wheel(event):
        if isinstance(event.widget,ttk.Treeview):
            return
        scroll = scroll_canvases[tabs.index('current')]
        if scroll.bbox('all') and scroll.bbox('all')[3]>scroll.winfo_height():
            scroll.yview_scroll(-int(event.delta/120),'units')
    root.bind_all('<MouseWheel>',wheel)
    ttk.Label(sidebar,textvariable=connection,style='Sidebar.TLabel',wraplength=160).pack(side='bottom',anchor='w',padx=10,pady=12)
    ttk.Label(sidebar,text='F6  Pause session\nF8  Disable tracking',style='Sidebar.TLabel').pack(side='bottom',anchor='w',padx=10,pady=12)

    metrics = [tk.StringVar(value='—') for _ in range(3)]
    cards = ttk.Frame(overview)
    cards.pack(fill='x',pady=(0,18))
    for i,title in enumerate(('PLAYERS','READ TIME','ACTIVE WEAPON')):
        card = ttk.Frame(cards,padding=14,style='Sidebar.TFrame')
        card.grid(row=0,column=i,sticky='nsew',padx=(0,8))
        cards.columnconfigure(i,weight=1)
        ttk.Label(card,text=title,style='Sidebar.TLabel',font=('Segoe UI',9)).pack(anchor='w')
        ttk.Label(card,textvariable=metrics[i],style='Sidebar.TLabel',foreground='#e5e7eb',font=('Segoe UI',17,'bold')).pack(anchor='w',pady=(8,0))
    ttk.Label(overview,text='Position radar',font=('Segoe UI',14,'bold')).pack(anchor='w')
    ttk.Label(overview,text='World XY positions relative to you. This is not a map or visibility test.',foreground='#94a3b8',wraplength=620).pack(anchor='w',pady=(4,10))
    radar = tk.Canvas(overview,height=280,bg='#0b1120',highlightthickness=1,highlightbackground='#263449')
    radar.pack(fill='x')
    ttk.Label(overview,text='Radar range (world units)').pack(anchor='w',pady=(12,0))
    ttk.Scale(overview,from_=500,to=6000,variable=radar_range).pack(fill='x')
    ttk.Checkbutton(overview,text='Pause telemetry and tracking',variable=paused).pack(anchor='w',pady=12)
    for parent,text,var in [(visuals,'Player boxes and health',esp),
                           (visuals,'Include teammates in ESP',teammates),
                           (visuals,'Skeleton — requires verified bone profile',skeleton),
                           (visuals,'Distance labels',show_distance),
                           (visuals,'Corner boxes',corners),
                           (visuals,'Player names',show_names),
                           (visuals,'Weapon and ammo labels',show_weapons),
                           (visuals,'Armor bars',show_armor),
                           (visuals,'Snaplines',snaplines),
                           (visuals,'Center crosshair',crosshair),
                           (tracking,'Enable aimbot — hold LEFT mouse',aim),
                           (tracking,'Allow aiming at teammates',aim_teammates),
                           (tracking,'Also hold Alt to align before a tap shot',preaim),
                           (tracking,'Show aim radius',show_radius)]:
        ttk.Checkbutton(parent,text=text,variable=var).pack(anchor='w',pady=7)
    ttk.Label(tracking,text='Weapon preset (manual)').pack(anchor='w',pady=(8,4))
    preset_box = ttk.Combobox(tracking,textvariable=preset,values=('Rifle','Sniper','Desert Eagle / Tap'),state='readonly')
    preset_box.pack(fill='x')
    def apply_preset(unused=None):
        values = {'Rifle':(180,.14,'Chest'), 'Sniper':(90,.075,'Chest'),
                  'Desert Eagle / Tap':(110,.085,'Head')}
        r,t,joint = values[preset.get()]
        radius.set(r)
        tau.set(t)
        aim_joint.set(joint)
        gain.set(1.0)
    preset_box.bind('<<ComboboxSelected>>',apply_preset)
    ttk.Checkbutton(tracking,text='Choose preset from active weapon',variable=automatic_preset).pack(anchor='w',pady=5)
    ttk.Label(tracking,text='Aim point').pack(anchor='w',pady=(10,4))
    ttk.Combobox(tracking,textvariable=aim_joint,values=('Chest','Head'),state='readonly').pack(fill='x')
    for parent,text,var,low,high in [(tracking,'Aim radius (screen pixels)',radius,30,500),
                            (tracking,'Smoothing time (seconds)',tau,.04,.6),
                            (tracking,'Input calibration multiplier',gain,.25,2),
                            (visuals,'Fallback standing height (world units)',height,40,80)]:
        label = ttk.Label(parent)
        label.pack(anchor='w',pady=(10,0))
        def refresh(*unused, label=label,text=text,var=var):
            label.configure(text=f'{text}: {var.get():.2f}')
        var.trace_add('write',refresh)
        refresh()
        ttk.Scale(parent,from_=low,to=high,variable=var).pack(fill='x')
    ttk.Label(visuals,text='Boxes follow collision bounds when available.\nSkeleton stays hidden unless valid world-bone data is loaded.',
              foreground='#94a3b8',wraplength=440).pack(anchor='w',pady=18)
    ttk.Label(visuals,text='Overlay appearance',font=('Segoe UI',14,'bold')).pack(anchor='w',pady=(6,12))
    ttk.Combobox(visuals,textvariable=palette,values=('Emerald','Blue','Rose','Amber'),state='readonly').pack(fill='x',pady=5)
    for text,var,low,high in [('Maximum distance (0 = unlimited)',max_distance,0,10000),
                            ('Overlay opacity',overlay_alpha,.45,1),
                            ('Label size',label_size,8,16),('Line width',line_width,1,4)]:
        label = ttk.Label(visuals)
        label.pack(anchor='w',pady=(10,0))
        def update_label(*unused,label=label,text=text,var=var):
            label.configure(text=f'{text}: {var.get():.2f}')
        var.trace_add('write',update_label)
        update_label()
        ttk.Scale(visuals,from_=low,to=high,variable=var).pack(fill='x')
    ttk.Label(tracking,text='Use Alt to align before firing a tap shot.\nScope scale and sensitivity are read each frame.\nHead / chest remain approximate without bone data.\nNo occlusion, spread, recoil, or hit-registration correction.',
              foreground='#94a3b8',wraplength=440).pack(anchor='w',pady=18)
    ttk.Label(practice,text='Local server accuracy',font=('Segoe UI',16,'bold')).pack(anchor='w',pady=(0,12))
    ttk.Label(practice,text='These controls copy commands for your local server console.\nThey do not change the running game automatically.\nThe server must allow these settings. Check console errors.',
              wraplength=440).pack(anchor='w',pady=(0,18))
    practice_status = tk.StringVar(value='No commands applied by this app.')
    def copy_practice(commands):
        root.clipboard_clear()
        root.clipboard_append(commands)
        practice_status.set('Copied. Paste into the CS2 console on your local map.')
    for label,commands in [
        ('Copy: no spread', 'sv_cheats 1; weapon_accuracy_nospread 1'),
        ('Copy: run installed practice config', 'exec research_accuracy')]:
        ttk.Button(practice,text=label,command=lambda commands=commands:copy_practice(commands)).pack(fill='x',pady=5)
    ttk.Label(practice,textvariable=practice_status,foreground='#94a3b8',wraplength=440).pack(anchor='w',pady=14)
    ttk.Label(practice,text='No spread requests removal of random shot deviation.\nNo recoil is unavailable: this build rejected both recoil commands.\nThe app has no verified recoil-compensation implementation.\nThese commands do not change hit registration.\nInspect the game console to verify command acceptance.',
              wraplength=440).pack(anchor='w',pady=12)
    ttk.Label(diagnostics,text='Dump folder',font=('Segoe UI',12,'bold')).pack(anchor='w')
    ttk.Label(diagnostics,textvariable=source_folder,wraplength=620).pack(anchor='w',pady=6)
    ttk.Label(diagnostics,textvariable=details,wraplength=620).pack(anchor='w',pady=12)
    ttk.Label(diagnostics,text='Use Windowed / Fullscreen Windowed.\nInput stops when CS2 loses focus.\nIf input fails, tracking switches off.',wraplength=440).pack(anchor='w',pady=12)
    ttk.Separator(panel).pack(fill='x',pady=(12,8))
    ttk.Label(panel,textvariable=status,wraplength=510).pack(anchor='w',pady=(0,8))

    columns = ('name','health','armor','weapon','ammo','distance','scoped')
    search = ttk.Frame(players_tab)
    search.pack(fill='x',pady=(0,12))
    ttk.Label(search,text='Search player or weapon').pack(side='left',padx=(0,10))
    ttk.Entry(search,textvariable=query).pack(side='left',fill='x',expand=True)
    table_frame = ttk.Frame(players_tab)
    table_frame.pack(fill='both',expand=True)
    table = ttk.Treeview(table_frame,columns=columns,show='headings',height=12)
    for column,title,width in [('name','Player',130),('health','HP',45),('armor','Armor',50),
                               ('weapon','Weapon',100),('ammo','Ammo',50),('distance','Distance (u)',85),('scoped','Scoped',65)]:
        table.heading(column,text=title)
        table.column(column,width=width,minwidth=40,stretch=True)
    table.grid(row=0,column=0,sticky='nsew')
    vertical = ttk.Scrollbar(table_frame,orient='vertical',command=table.yview)
    vertical.grid(row=0,column=1,sticky='ns')
    horizontal = ttk.Scrollbar(table_frame,orient='horizontal',command=table.xview)
    horizontal.grid(row=1,column=0,sticky='ew')
    table.configure(yscrollcommand=vertical.set,xscrollcommand=horizontal.set)
    table_frame.rowconfigure(0,weight=1)
    table_frame.columnconfigure(0,weight=1)
    ttk.Label(players_tab,text='Live player telemetry. Distance is in engine world units.',
              foreground='#94a3b8',wraplength=620).pack(anchor='w',pady=10)

    overlay = tk.Toplevel(root)
    overlay.withdraw()
    overlay.overrideredirect(True)
    overlay.configure(bg='#ff00ff')
    overlay.attributes('-transparentcolor','#ff00ff')
    canvas = tk.Canvas(overlay,bg='#ff00ff',highlightthickness=0)
    canvas.pack(fill='both',expand=True)
    overlay.update_idletasks()
    overlay_hwnd = wg.GetParent(overlay.winfo_id()) or overlay.winfo_id()
    ex = wg.GetWindowLong(overlay_hwnd,wc.GWL_EXSTYLE)
    wg.SetWindowLong(overlay_hwnd,wc.GWL_EXSTYLE,ex | wc.WS_EX_LAYERED |
                     wc.WS_EX_TRANSPARENT | wc.WS_EX_NOACTIVATE | wc.WS_EX_TOOLWINDOW)
    send_mouse = make_mouse_sender()
    state = dict(reader=None,hwnd=None,last=time.monotonic(),retry=0,
                 target=None,residual=(0,0),closed=False,next_frame=0,table_time=0,
                 latest=None,auto_weapon=None,recorder=None,record_time=0,
                 sort_by='name',descending=False,last_error=None,connected=False)

    def sort_players(column):
        state['descending'] = not state['descending'] if state['sort_by']==column else False
        state['sort_by'] = column
        state['table_time'] = 0
    for column in columns:
        table.heading(column,command=lambda column=column:sort_players(column))
    query.trace_add('write',lambda *unused:state.update(table_time=0))

    def stop_recording():
        recorder = state['recorder']
        if recorder is not None:
            state['recorder'] = None
            try:
                recorder.close()
                recording_status.set(f'Saved {recorder.frames} frames to {recorder.path.name}')
            except OSError as error:
                recording_status.set(f'Recording close failed: {error}')

    def disconnect():
        overlay.withdraw()
        state['target'] = None
        state['residual'] = (0,0)
        state['latest'] = None
        state['auto_weapon'] = None
        state['connected'] = False
        connection.set('DISCONNECTED')
        metrics[0].set('—')
        metrics[1].set('—')
        metrics[2].set('—')
        radar.delete('all')
        stop_recording()
        table.delete(*table.get_children())
        if state['reader'] is not None:
            state['reader'].close()
        state.update(reader=None,hwnd=None,retry=time.monotonic()+2)

    def close():
        save_preferences()
        state['closed'] = True
        disconnect()
        root.destroy()

    root.protocol('WM_DELETE_WINDOW',close)
    ttk.Button(panel,text='Reconnect',command=disconnect).pack(side='left',pady=8)
    ttk.Button(panel,text='Exit',command=close).pack(side='right',pady=8)

    def hotkeys():
        # Rising-edge polling works while the game, rather than the panel, has focus.
        keys = [False,False]
        def poll():
            if state['closed']:
                return
            for i,key in enumerate((wc.VK_F6,wc.VK_F8)):
                down = bool(wa.GetAsyncKeyState(key)&0x8000)
                if down and not keys[i]:
                    if i==0:
                        paused.set(not paused.get())
                    else:
                        aim.set(False)
                keys[i] = down
            root.after(40,poll)
        poll()

    preferences = Path(__file__).with_name('pika_op_settings.json')
    settings_vars = {name:var for name,var in [
        ('esp',esp),('teammates',teammates),('aim_teammates',aim_teammates),('skeleton',skeleton),('show_distance',show_distance),
        ('show_radius',show_radius),('preaim',preaim),('corners',corners),('show_names',show_names),
        ('show_weapons',show_weapons),('show_armor',show_armor),('snaplines',snaplines),
        ('automatic_preset',automatic_preset),('crosshair',crosshair),('max_distance',max_distance),
        ('overlay_alpha',overlay_alpha),('label_size',label_size),('line_width',line_width),
        ('radar_range',radar_range),('palette',palette),('source_folder',source_folder),
        ('preset',preset),('aim_joint',aim_joint),('radius',radius),('tau',tau),('gain',gain),('height',height)]}
    defaults = {name:var.get() for name,var in settings_vars.items()}
    def save_preferences():
        try:
            write_settings(preferences,{name:var.get() for name,var in settings_vars.items()})
            status.set('Settings saved')
        except (OSError,ValueError) as error:
            status.set(f'Could not save settings: {error}')
    def reset_preferences():
        aim.set(False)
        for name,var in settings_vars.items():
            var.set(defaults[name])
        disconnect()
        save_preferences()
    ttk.Button(diagnostics,text='Save settings',command=save_preferences).pack(fill='x',pady=5)
    ttk.Button(diagnostics,text='Reset settings',command=reset_preferences).pack(fill='x',pady=5)
    ttk.Label(diagnostics,text='Settings save on exit. Tracking always starts disabled.',foreground='#94a3b8').pack(anchor='w',pady=8)
    event_log = tk.Text(diagnostics,height=7,bg='#0b1120',fg='#94a3b8',insertbackground='white',
                        font=('Consolas',9),relief='flat',wrap='word',state='disabled')
    event_log.pack(fill='x',pady=10)
    def log_event(message):
        event_log.configure(state='normal')
        event_log.insert('end',datetime.now(timezone.utc).strftime('%H:%M:%S UTC')+'  '+message+'\n')
        if int(event_log.index('end-1c').split('.')[0])>50:
            event_log.delete('1.0','2.0')
        event_log.see('end')
        event_log.configure(state='disabled')

    ttk.Label(recording,text='Record an experiment',font=('Segoe UI',17,'bold')).pack(anchor='w',pady=(0,12))
    ttk.Label(recording,text='Save raw frames as JSON Lines at up to 10 Hz.\nRecording works with the panel focused. Pausing creates a gap.\nA disconnect stops recording and closes the file.',wraplength=620).pack(anchor='w',pady=(0,18))
    ttk.Label(recording,textvariable=recording_status,foreground='#6ee7b7',wraplength=620).pack(anchor='w',pady=12)
    def start_recording():
        if state['recorder'] is not None:
            return
        if state['latest'] is None:
            recording_status.set('Connect to a local map first')
            return
        path = filedialog.asksaveasfilename(title='New recording file',defaultextension='.jsonl',
            initialfile='pika_op_'+datetime.now(timezone.utc).strftime('%Y%m%d_%H%M%S')+'.jsonl',filetypes=[('JSON Lines','*.jsonl')])
        if path:
            try:
                state['recorder'] = Recorder(path)
                state['record_time'] = 0
                recording_status.set('Recording to '+Path(path).name)
            except OSError as error:
                recording_status.set(f'Cannot start recording: {error}; use a new filename')
    ttk.Button(recording,text='Start recording',command=start_recording).pack(fill='x',pady=5)
    ttk.Button(recording,text='Stop recording',command=stop_recording).pack(fill='x',pady=5)
    def export_player_csv():
        if state['latest'] is None:
            status.set('No valid frame to export')
            return
        path = filedialog.asksaveasfilename(title='New player CSV',defaultextension='.csv',
            initialfile='pika_op_players.csv',filetypes=[('CSV','*.csv')])
        if path:
            try:
                export_csv(Path(path),state['latest'])
                status.set('Player CSV saved')
            except OSError as error:
                status.set(f'CSV export failed: {error}; use a new filename')
    ttk.Button(recording,text='Export current players as CSV',command=export_player_csv).pack(fill='x',pady=5)
    ttk.Button(players_tab,text='Export player CSV',command=export_player_csv).pack(fill='x',pady=5)

    def choose_folder():
        selected = filedialog.askdirectory(title='Select cs2-dumper folder or output folder')
        if selected:
            try:
                source_folder.set(str(resolve_dump_folder(selected)))
                disconnect()
                state['retry'] = 0
            except (OSError,ValueError) as error:
                status.set(str(error))

    def export_snapshot():
        if state['latest'] is None:
            status.set('No valid frame to export; enter a local map first')
            return
        path = filedialog.asksaveasfilename(title='Save telemetry snapshot',
            defaultextension='.json',initialfile='telemetry_snapshot.json',filetypes=[('JSON','*.json')])
        if path:
            try:
                Path(path).write_text(json.dumps(state['latest'],indent=2),encoding='utf-8')
                status.set('Telemetry snapshot saved')
            except OSError as error:
                status.set(f'Export failed: {error}')

    def export_inventory():
        try:
            report = inspect_dump(source_folder.get())
            path = filedialog.asksaveasfilename(title='Save dump inventory',
                defaultextension='.json',initialfile='dump_inventory.json',filetypes=[('JSON','*.json')])
            if path:
                Path(path).write_text(json.dumps(report,indent=2),encoding='utf-8')
                status.set('Dump inventory saved')
        except (OSError,ValueError) as error:
            status.set(f'Inventory failed: {error}')
    ttk.Button(diagnostics,text='Select dump folder',command=choose_folder).pack(fill='x',pady=5)
    ttk.Button(diagnostics,text='Export dump inventory',command=export_inventory).pack(fill='x',pady=5)
    ttk.Button(players_tab,text='Export current snapshot',command=export_snapshot).pack(fill='x',pady=5)

    def tick():
        if state['closed']:
            return
        now = time.monotonic()
        dt,state['last'] = now-state['last'],now
        try:
            if paused.get():
                overlay.withdraw()
                state['target'],state['residual'] = None,(0,0)
                connection.set('PAUSED')
                status.set('Session paused · press F6 to resume')
                return
            if state['reader'] is None:
                if now < state['retry']:
                    return
                state['reader'] = Reader(source_folder.get())
                windows = []
                def collect(hwnd,unused):
                    if wg.IsWindowVisible(hwnd) and wp.GetWindowThreadProcessId(hwnd)[1] == state['reader'].pm.process_id:
                        l,t,r,b = wg.GetClientRect(hwnd)
                        if r>l and b>t:
                            windows.append(((r-l)*(b-t),hwnd))
                wg.EnumWindows(collect,None)
                if not windows:
                    raise ValueError('No visible CS2 window')
                state['hwnd'] = max(windows)[1]
            hwnd = state['hwnd']
            if not wg.IsWindow(hwnd):
                raise ValueError('CS2 window closed')
            foreground = not wg.IsIconic(hwnd) and wg.GetForegroundWindow() == hwnd
            if not foreground:
                overlay.withdraw()
                state['target'],state['residual'] = None,(0,0)
                if now < state['next_frame']:
                    return
            l,t,r,b = wg.GetClientRect(hwnd)
            width,screen_height = r-l,b-t
            if width<=0 or screen_height<=0:
                return
            read_start = time.perf_counter()
            origin,team,matrix,players = state['reader'].frame()
            read_ms = (time.perf_counter()-read_start)*1000
            state['next_frame'] = now+(.1 if state['recorder'] else .25)
            reader = state['reader']
            connection.set('CONNECTED')
            if not state['connected']:
                state['connected'] = True
                state['last_error'] = None
                log_event(f'Connected to build {reader.build}')
            state['latest'] = dict(timestamp_utc=datetime.now(timezone.utc).isoformat(),
                monotonic_seconds=now,build=reader.build,dump_info=reader.info,
                entity_stride=reader.stride,local_position=origin,local=reader.local_data,
                viewport=[width,screen_height],matrix_convention='row-major M * column vector',
                view_projection=matrix,players=players)
            if state['recorder'] is not None and now-state['record_time'] >= .1:
                try:
                    state['recorder'].append(state['latest'])
                    state['record_time'] = now
                    recording_status.set(f'Recording · {state["recorder"].frames} frames · {state["recorder"].path.name}')
                except (OSError,ValueError) as error:
                    stop_recording()
                    recording_status.set(f'Recording stopped: {error}')
                    log_event(f'Recording stopped: {error}')
            bone_count = sum(bool(p['bones']) for p in players)
            bone_status = f'{bone_count} skeletons validated' if reader.skeleton else 'Unavailable — no verified bone profile'
            local_weapon = reader.local_data['weapon']
            metrics[0].set(str(len(players)))
            metrics[1].set(f'{read_ms:.1f} ms')
            metrics[2].set(local_weapon['name'])
            if tabs.index('current')==0:
                rw,rh = max(100,radar.winfo_width()),max(100,radar.winfo_height())
                cx,cy = rw/2,rh/2
                extent = min(rw,rh)/2-22
                radar.delete('all')
                for fraction in (.5,1):
                    radius_px = extent*fraction
                    radar.create_oval(cx-radius_px,cy-radius_px,cx+radius_px,cy+radius_px,outline='#263449')
                radar.create_line(cx-extent,cy,cx+extent,cy,fill='#263449')
                radar.create_line(cx,cy-extent,cx,cy+extent,fill='#263449')
                radar.create_text(18,16,anchor='nw',text='+Y up / +X right',fill='#64748b',font=('Segoe UI',9))
                radar.create_oval(cx-4,cy-4,cx+4,cy+4,fill='#f8fafc',outline='')
                for p in players:
                    dx,dy = p['position'][0]-origin[0],p['position'][1]-origin[1]
                    if math.hypot(dx,dy)>radar_range.get():
                        continue
                    rx,ry = cx+dx/radar_range.get()*extent,cy-dy/radar_range.get()*extent
                    color = '#60a5fa' if p['team']==team else '#34d399'
                    radar.create_oval(rx-4,ry-4,rx+4,ry+4,fill=color,outline='')
            details.set(f'Running build: {reader.build} | dump build: {reader.info.get("build_number", "unknown")}\n'
                f'Dump timestamp: {reader.info.get("timestamp", "unknown")}\n'
                f'Validated stride: 0x{reader.stride:X}\nBones: {bone_status}\n'
                f'Local weapon: {local_weapon["name"]} | ammo: {local_weapon["ammo"]}\n'
                f'Scoped: {reader.local_data["scoped"]} | shots: {reader.local_data["shots"]}\n'
                'Position: '+', '.join(f'{v:.1f}' for v in origin))
            if now-state['table_time'] >= .25:
                state['table_time'] = now
                rows = filtered_players(players,origin,query.get(),max_distance.get(),state['sort_by'],state['descending'])
                keep = set()
                for index,p in enumerate(rows):
                    identifier = str(p['address'])
                    keep.add(identifier)
                    values = (p['name'],p['health'],p['armor'] if p['armor'] is not None else '?',
                        p['weapon']['name'],p['weapon']['ammo'] if p['weapon']['ammo'] is not None else '?',
                        f'{math.dist(origin,p["position"]):.0f}',p['scoped'])
                    if table.exists(identifier):
                        table.item(identifier,values=values)
                        table.move(identifier,'',index)
                    else:
                        table.insert('',index,iid=identifier,values=values)
                for identifier in table.get_children():
                    if identifier not in keep:
                        table.delete(identifier)
            if automatic_preset.get() and state['auto_weapon'] != local_weapon['name']:
                state['auto_weapon'] = local_weapon['name']
                weapon_name = local_weapon['name']
                preset.set('Sniper' if weapon_name in ('awp','ssg08','scar20','g3sg1') else
                           'Desert Eagle / Tap' if weapon_name in ('deagle','revolver') else 'Rifle')
                apply_preset()
            if not foreground:
                status.set(f'Connected · {len(players)} players · focus CS2 for overlay')
                return
            x,y = wg.ClientToScreen(hwnd,(0,0))
            overlay.geometry(f'{width}x{screen_height}{x:+d}{y:+d}')
            # Show through Win32, preserving the foreground window.
            wg.SetWindowPos(overlay_hwnd,wc.HWND_TOPMOST,x,y,width,screen_height,
                            wc.SWP_NOACTIVATE | wc.SWP_SHOWWINDOW)
            overlay.attributes('-alpha',overlay_alpha.get())
            canvas.delete('all')
            center = (width/2,screen_height/2)
            if crosshair.get():
                cx,cy = center
                canvas.create_line(cx-7,cy,cx+7,cy,fill='#e5e7eb',width=1)
                canvas.create_line(cx,cy-7,cx,cy+7,fill='#e5e7eb',width=1)
            accents = {'Emerald':'#34d399','Blue':'#38bdf8','Rose':'#fb7185','Amber':'#fbbf24'}
            stroke = max(1,round(line_width.get()))
            font = ('Segoe UI',round(label_size.get()))
            candidates = []
            for player in players:
                if player['team']==team and not (teammates.get() or aim_teammates.get()):
                    continue
                distance = math.dist(origin,player['position'])
                if max_distance.get()>0 and distance>max_distance.get():
                    continue
                px,py,pz = player['position']
                minimum = player['minimum']
                maximum = player['maximum'] if player['bounds_valid'] else (16,16,height.get())
                feet = project((px,py,pz),matrix,width,screen_height)
                top = project((px,py,pz+height.get()),matrix,width,screen_height)
                bones = player['bones']
                joint_name = aim_joint.get().lower()
                aim_world = bones.get(joint_name)
                if aim_world is None:
                    low,high = minimum[2],maximum[2]
                    fraction = .92 if joint_name=='head' else .70
                    aim_world = (px,py,pz+low+(high-low)*fraction)
                aim_point = project(aim_world,matrix,width,screen_height)
                if aim_point and (player['team'] != team or aim_teammates.get()):
                    candidates.append(dict(player,aim=aim_point))
                if player['team']==team and not teammates.get():
                    continue
                color = '#60a5fa' if player['team']==team else accents[palette.get()]
                if esp.get() and skeleton.get() and bones:
                    profile = state['reader'].skeleton
                    for a,b in profile.get('edges',[]):
                        first = project(bones[a],matrix,width,screen_height)
                        second = project(bones[b],matrix,width,screen_height)
                        if first and second:
                            canvas.create_line(*first,*second,fill='#080c12',width=4)
                            canvas.create_line(*first,*second,fill=color,width=stroke)
                if not esp.get():
                    continue
                bounds = projected_bounds(player['position'],minimum,maximum,
                                          matrix,width,screen_height)
                if bounds:
                    left,box_top,right,box_bottom = bounds
                else:
                    if not feet or not top:
                        continue
                    box_top,box_bottom = top[1],feet[1]
                    half_width = (box_bottom-box_top)*.23
                    left,right = top[0]-half_width,top[0]+half_width
                box_height = box_bottom-box_top
                if box_height < 3:
                    continue
                if state['target'] == player['address']:
                    color = '#fbbf24'
                if corners.get():
                    length = min((right-left)*.25,box_height*.15)
                    for cx,cy,dx,dy in [(left,box_top,1,1),(right,box_top,-1,1),
                                       (left,box_bottom,1,-1),(right,box_bottom,-1,-1)]:
                        coordinates = (cx+dx*length,cy,cx,cy,cx,cy+dy*length)
                        canvas.create_line(*coordinates,fill='#080c12',width=stroke+2)
                        canvas.create_line(*coordinates,fill=color,width=stroke)
                else:
                    canvas.create_rectangle(left,box_top,right,box_bottom,outline='#080c12',width=stroke+2)
                    canvas.create_rectangle(left,box_top,right,box_bottom,outline=color,width=stroke)
                canvas.create_line(left-5,box_bottom,left-5,box_top,fill='#111827',width=4)
                canvas.create_line(left-5,box_bottom,left-5,
                    box_bottom-box_height*min(player['health'],100)/100,fill=color,width=2)
                if show_armor.get() and player['armor'] is not None:
                    canvas.create_line(left,box_bottom+5,right,box_bottom+5,fill='#111827',width=4)
                    canvas.create_line(left,box_bottom+5,left+(right-left)*min(player['armor'],100)/100,
                                       box_bottom+5,fill='#60a5fa',width=2)
                if snaplines.get():
                    canvas.create_line(width/2,screen_height,left+(right-left)/2,box_bottom,fill=color,width=1)
                distance = math.dist(origin,player['position'])
                label = (player['name']+' | ' if show_names.get() else '')+f"{player['health']} HP" + (f' | {distance:.0f} u' if show_distance.get() else '')
                label_y = max(12,box_top-12)
                canvas.create_text((left+right)/2+1,label_y+1,text=label,fill='#080c12',font=(*font,'bold'))
                canvas.create_text((left+right)/2,label_y,text=label,
                                   fill='white',font=font)
                if show_weapons.get():
                    weapon = player['weapon']
                    text = weapon['name']+(f' [{weapon["ammo"]}]' if weapon['ammo'] is not None and weapon['ammo']>=0 else '')
                    canvas.create_text((left+right)/2,box_bottom+17,text=text,
                                       fill='#cbd5e1',font=font)
            def activation_held():
                return bool(wa.GetAsyncKeyState(wc.VK_LBUTTON)&0x8000 or
                            (preaim.get() and wa.GetAsyncKeyState(wc.VK_MENU)&0x8000))
            held = activation_held()
            target = choose_target(candidates,center,radius.get(),state['target']) if aim.get() and held else None
            if aim.get() and show_radius.get():
                cx,cy = center
                canvas.create_oval(cx-radius.get(),cy-radius.get(),cx+radius.get(),cy+radius.get(),
                                   outline='#64748b',width=1)
            if target is not None:
                if state['target'] != target['address']:
                    state['residual'] = (0,0)
                sensitivity,scope_scale,scoped = state['reader'].input_calibration()
                movement,state['residual'] = calibrated_step(target['aim'],center,matrix,
                    sensitivity,scope_scale,dt,tau.get(),gain.get(),state['residual'])
                # Recheck immediately before emitting input.
                if wg.GetForegroundWindow()==hwnd and activation_held():
                    send_mouse(*movement)
                state['target'] = target['address']
                ax,ay = target['aim']
                canvas.create_oval(ax-4,ay-4,ax+4,ay+4,outline='#fbbf24',width=2)
            else:
                state['target'],state['residual'] = None,(0,0)
            canvas.create_text(18,18,anchor='nw',text=f'pika op | {len(players)} players | LMB tracking: {"ON" if aim.get() else "OFF"}',
                               fill='#e5e7eb',font=('Segoe UI',11,'bold'))
            status.set(f'Connected · {len(players)} players · {len(candidates)} on-screen candidates')
        except Exception as error:
            # Visible failure and reconnect; never keep issuing input on stale data.
            status.set(f'{type(error).__name__}: {error}')
            if state['last_error'] != str(error):
                log_event(f'{type(error).__name__}: {error}')
                state['last_error'] = str(error)
            aim.set(False)
            disconnect()
        finally:
            if not state['closed']:
                root.after(16,tick)
    try:
        saved = read_settings(preferences,defaults)
        ranges = {'radius':(30,500),'tau':(.04,.6),'gain':(.25,2),'height':(40,80),
                  'max_distance':(0,10000),'overlay_alpha':(.45,1),'label_size':(8,16),
                  'line_width':(1,4),'radar_range':(500,6000)}
        for name,var in settings_vars.items():
            value = saved[name]
            if name in ranges:
                value = max(ranges[name][0],min(ranges[name][1],value))
            if name in ('palette','preset','aim_joint'):
                allowed = {'palette':('Emerald','Blue','Rose','Amber'),
                           'preset':('Rifle','Sniper','Desert Eagle / Tap'),'aim_joint':('Chest','Head')}
                if value not in allowed[name]:
                    value = defaults[name]
            var.set(value)
    except (OSError,ValueError) as error:
        log_event(f'Settings not loaded: {error}')
    hotkeys()
    root.after(100,tick)
    root.mainloop()


if __name__ == '__main__':
    main()
