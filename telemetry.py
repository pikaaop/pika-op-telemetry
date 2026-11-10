"""Read-only telemetry for an owned, local Windows debug session."""
import argparse
import json
import math
from pathlib import Path
import struct
import time

DEFAULT = Path(r"C:\Users\spide\Downloads\cs2-dumper-main\cs2-dumper-main\output")


def project(position, matrix, width, height, clip_viewport=True):
    vector = (*position, 1.0)
    clip = [sum(matrix[4*r+c] * vector[c] for c in range(4))
            for r in range(4)]
    if not all(map(math.isfinite, clip)) or clip[3] <= 1e-6:
        return None
    x, y = clip[0] / clip[3], clip[1] / clip[3]
    if clip_viewport and (abs(x) > 1 or abs(y) > 1):
        return None
    # Point marker only: depth convention and occlusion are not evaluated.
    return ((x + 1) * width / 2, (1 - y) * height / 2)


def angles(camera, target, previous_yaw):
    x, y, z = (b-a for a, b in zip(camera, target))
    distance = math.hypot(x, y, z)
    if distance < 1e-9:
        return None
    yaw = math.atan2(y, x) if math.hypot(x, y) > 1e-9 else previous_yaw
    return (-math.asin(max(-1, min(1, z / distance))), yaw)


def smooth(current, goal, dt, tau):
    alpha = -math.expm1(-dt / tau)
    return (current[0] + alpha * (goal[0] - current[0]),
            math.remainder(current[1] + alpha * math.remainder(
                goal[1] - current[1], 2 * math.pi), 2 * math.pi))


def self_test():
    identity = (1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1)
    assert project((0,0,0.5), identity, 800,600) == (400,300)
    assert project((2,0,0.5), identity, 800,600) is None
    assert angles((0,0,0), (0,0,0), 0) is None
    assert math.isclose(angles((0,0,0), (0,1,0), 0)[1], math.pi/2)
    start = (0, math.radians(179))
    result = smooth(start, (0, math.radians(-179)), .01, .1)
    assert 0 < result[1] - start[1] < math.radians(2)
    print("Math checks passed")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pid', type=int)
    parser.add_argument('--offset-dir', type=Path, default=DEFAULT)
    parser.add_argument('--target-address', type=lambda s: int(s, 0),
                        help='Known C_BaseEntity-compatible object address')
    parser.add_argument('--overlay', action='store_true')
    parser.add_argument('--hwnd', type=lambda s: int(s, 0),
                        help='Optional debug window handle; otherwise discovered by PID')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return

    import pymem
    import pymem.process

    offsets = json.loads((args.offset_dir / 'offsets.json').read_text())['client.dll']
    classes = json.loads((args.offset_dir / 'client_dll.json').read_text())['client.dll']['classes']
    scene_offset = classes['C_BaseEntity']['fields']['m_pGameSceneNode']
    position_offset = classes['CGameSceneNode']['fields']['m_vecAbsOrigin']
    # The schema identifies this as a network vector wrapper. Verify its
    # component layout before using it as a raw three-float camera offset.
    pm = pymem.Pymem(args.pid or 'cs2.exe')
    pygame = None
    try:
        module = pymem.process.module_from_name(pm.process_handle, 'client.dll')
        if module is None:
            raise RuntimeError('client.dll not loaded in selected process')
        base = module.lpBaseOfDll

        def pointer(address):
            return struct.unpack('<Q', pm.read_bytes(address, 8))[0]

        def position(entity):
            if not entity:
                raise ValueError('Null object pointer')
            scene = pointer(entity + scene_offset)
            if not scene:
                raise ValueError('Null scene pointer')
            value = struct.unpack('<3f', pm.read_bytes(scene + position_offset, 12))
            if not all(map(math.isfinite, value)):
                raise ValueError('Invalid position')
            return value

        if args.overlay:
            import pygame
            import win32api
            import win32con as wc
            import win32gui as wg
            import ctypes
            ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
            if not args.hwnd:
                windows = []
                def collect_window(hwnd, unused):
                    _, pid = __import__('win32process').GetWindowThreadProcessId(hwnd)
                    if pid == pm.process_id and wg.IsWindowVisible(hwnd):
                        l,t,r,b = wg.GetClientRect(hwnd)
                        if r > l and b > t:
                            windows.append(((r-l)*(b-t), hwnd))
                wg.EnumWindows(collect_window, None)
                if not windows:
                    raise RuntimeError('No visible CS2 window; open the game first')
                args.hwnd = max(windows)[1]
            _, owner_pid = __import__('win32process').GetWindowThreadProcessId(args.hwnd)
            if owner_pid != pm.process_id:
                raise ValueError('Window belongs to another process')
            pygame.display.init()
            pygame.font.init()
            font = pygame.font.Font(None, 26)
            left,top,right,bottom = wg.GetClientRect(args.hwnd)
            if right <= left or bottom <= top:
                raise ValueError('Debug window has no drawable client area')
            surface = pygame.display.set_mode((right-left,bottom-top), pygame.NOFRAME | pygame.HIDDEN)
            overlay = pygame.display.get_wm_info()['window']
            style = wg.GetWindowLong(overlay, wc.GWL_EXSTYLE)
            wg.SetWindowLong(overlay, wc.GWL_EXSTYLE,
                (style & ~wc.WS_EX_APPWINDOW) | wc.WS_EX_LAYERED |
                wc.WS_EX_TRANSPARENT | wc.WS_EX_NOACTIVATE | wc.WS_EX_TOOLWINDOW)
            wg.SetLayeredWindowAttributes(overlay, win32api.RGB(255,0,255),255,wc.LWA_COLORKEY)

        print('Read-only. Ctrl+C stops. Angles use object origin, not actual camera position.')
        previous = time.monotonic()
        last_output = 0
        current = None
        while True:
            now = time.monotonic()
            dt, previous = now - previous, now
            local = position(pointer(base + offsets['dwLocalPlayerPawn']))
            target = position(args.target_address) if args.target_address else None
            matrix = struct.unpack('<16f', pm.read_bytes(base + offsets['dwViewMatrix'],64))
            if not all(map(math.isfinite, matrix)):
                raise ValueError('Invalid matrix')
            if target is not None:
                goal = angles(local, target, current[1] if current else 0)
                if goal is not None:
                    current = goal if current is None else smooth(current,goal,dt,.12)
            if now - last_output >= .25:
                print(json.dumps({'time':now, 'origin':local, 'target':target,
                    'smoothed_angles_degrees':list(map(math.degrees,current)) if current else None}))
                last_output = now
            if args.overlay:
                if any(e.type == pygame.QUIT for e in pygame.event.get()):
                    break
                if not wg.IsWindow(args.hwnd):
                    break
                if wg.IsIconic(args.hwnd):
                    wg.ShowWindow(overlay, wc.SW_HIDE)
                else:
                    left,top,right,bottom = wg.GetClientRect(args.hwnd)
                    width,height = right-left,bottom-top
                    if width > 0 and height > 0:
                        x,y = wg.ClientToScreen(args.hwnd,(left,top))
                        # Keep SDL's surface size synchronized with the native window.
                        if surface.get_size() != (width,height):
                            raise RuntimeError('Viewport resized; restart overlay to recreate surface')
                        wg.SetWindowPos(overlay,wc.HWND_TOPMOST,x,y,width,height,
                            wc.SWP_NOACTIVATE | wc.SWP_SHOWWINDOW)
                        surface.fill((255,0,255))
                        lines = [
                            'Telemetry connected | Ctrl+C in console stops',
                            'Position: ' + ', '.join(f'{v:.2f}' for v in local),
                            'Target supplied' if target is not None else
                            'No target: automatic player discovery is not implemented',
                        ]
                        for row, line in enumerate(lines):
                            surface.blit(font.render(line, True, (0,255,0), (0,0,0)),
                                         (20,20 + row*28))
                        pixel = project(target,matrix,width,height) if target else None
                        if pixel:
                            pygame.draw.circle(surface,(0,255,0),tuple(round(v) for v in pixel),7,2)
                        pygame.display.flip()
            time.sleep(1/60)
    finally:
        if pygame is not None:
            pygame.display.quit()
        pm.close_process()


if __name__ == '__main__':
    try:
        main()
    except KeyboardInterrupt:
        pass
