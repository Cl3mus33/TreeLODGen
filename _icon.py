import math
from PIL import Image, ImageDraw

# A bare tree shown as a wireframe mesh, the way a 3D editor displays an object's edges:
# a slender trunk that keeps forking into finer branches, each one a tube of rings and
# lengthwise edges, back edges dimmed, vertices marked on the larger limbs.
S = 1024
img = Image.new('RGBA', (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.rounded_rectangle((32, 32, S - 32, S - 32), radius=200, fill=(24, 27, 33, 255))

for i in range(1, 8):  # faint viewport grid
    p = 32 + i * (S - 64) // 8
    d.line([(p, 60), (p, S - 60)], fill=(38, 43, 52, 255), width=3)
    d.line([(60, p), (S - 60, p)], fill=(38, 43, 52, 255), width=3)

FRONT = (120, 226, 170, 255)
BACK = (52, 104, 86, 255)
VERTEX = (255, 170, 60, 255)
TILT = 0.34  # how much each ring opens up, as if seen from slightly above

back_lines, front_lines, vertices = [], [], []


def tube(path, segments, mark):
    """path: list of (x, y, radius) along the centre line."""
    rings = []
    for i, (x, y, r) in enumerate(path):
        a, b = path[max(0, i - 1)], path[min(len(path) - 1, i + 1)]
        tx, ty = b[0] - a[0], b[1] - a[1]
        length = math.hypot(tx, ty) or 1.0
        tx, ty = tx / length, ty / length
        nx, ny = -ty, tx
        ring = []
        for k in range(segments):
            ang = 2 * math.pi * k / segments + math.pi / segments
            c, s = math.cos(ang), math.sin(ang)
            ring.append((x + nx * r * c + tx * r * TILT * s, y + ny * r * c + ty * r * TILT * s, s))
        rings.append(ring)
    for i, ring in enumerate(rings):
        for k in range(segments):
            p, q = ring[k], ring[(k + 1) % segments]
            (front_lines if (p[2] + q[2]) / 2 < 0 else back_lines).append((p, q))
            if i + 1 < len(rings):
                (front_lines if p[2] < 0 else back_lines).append((p, rings[i + 1][k]))
            if mark and p[2] < 0:
                vertices.append(p)


def limb(x, y, angle, length, radius, depth, bend):
    """Grows one limb upward from (x, y), then forks. angle: 0 = straight up."""
    steps = 4 if depth == 0 else 3
    path, cx, cy, a = [(x, y, radius)], x, y, angle
    forks = []
    for i in range(1, steps + 1):
        a += bend / steps
        cx += math.sin(a) * length / steps
        cy -= math.cos(a) * length / steps
        r = radius * (1 - 0.62 * i / steps)
        path.append((cx, cy, r))
        forks.append((cx, cy, a, r))
    tube(path, 8 if depth == 0 else 6 if depth == 1 else 5, depth <= 1)
    if depth >= 3:
        return
    # one fork partway up to each side, and the limb carries on from its tip
    side = 1 if depth % 2 == 0 else -1
    for index, spread in ((len(forks) - 2, 0.72 * side), (len(forks) - 1, -0.62 * side), (len(forks) - 1, 0.16 * side)):
        fx, fy, fa, fr = forks[index]
        limb(fx, fy, fa + spread, length * 0.62, fr * 0.95, depth + 1, bend=-spread * 0.35)


# root flare, then the trunk and everything that grows from it
tube([(512, 912, 92), (512, 880, 62), (512, 840, 46)], 8, True)
limb(512, 840, 0.0, 330, 46, 0, bend=0.06)

for p, q in back_lines:
    d.line([(p[0], p[1]), (q[0], q[1])], fill=BACK, width=4)
for p, q in front_lines:
    d.line([(p[0], p[1]), (q[0], q[1])], fill=FRONT, width=7)
for p in vertices:
    d.ellipse((p[0] - 8, p[1] - 8, p[0] + 8, p[1] + 8), fill=VERTEX)

img = img.resize((256, 256), Image.LANCZOS)
img.save('TreeLODGen.ico', sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)])
img.save('_renders/icon_preview.png')
small = img.resize((32, 32), Image.LANCZOS).resize((128, 128), Image.NEAREST)
sheet = Image.new('RGBA', (256 + 16 + 128, 256), (255, 255, 255, 255))
sheet.paste(img, (0, 0), img)
sheet.paste(small, (272, 64), small)
sheet.save('_renders/icon_preview_sizes.png')
