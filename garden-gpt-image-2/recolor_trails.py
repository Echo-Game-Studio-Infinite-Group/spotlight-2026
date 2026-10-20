"""把竖版宣传图中的黄色刀光飘带换成蓝色能量带，其余像素保持不变。"""

from collections import deque

import numpy as np
from PIL import Image, ImageFilter

SRC = r"D:\Users\94880\Desktop\Echo Game Studio\1. 聚光灯Gamejam\物料\宣传图竖版无logo.png"
OUT = r"D:\Users\94880\Desktop\Echo Game Studio\1. 聚光灯Gamejam\物料\宣传图竖版无logo-蓝刀光-程序版.png"


def hsv_arrays(rgb01):
    r, g, b = rgb01[..., 0], rgb01[..., 1], rgb01[..., 2]
    mx = rgb01.max(2)
    mn = rgb01.min(2)
    diff = mx - mn
    h = np.zeros_like(mx)
    m = diff > 1e-6
    idx = m & (mx == r)
    h[idx] = ((g[idx] - b[idx]) / diff[idx]) % 6
    idx = m & (mx == g)
    h[idx] = (b[idx] - r[idx]) / diff[idx] + 2
    idx = m & (mx == b)
    h[idx] = (r[idx] - g[idx]) / diff[idx] + 4
    h *= 60
    s = np.where(mx > 0, diff / np.maximum(mx, 1e-6), 0)
    return h, s, mx


def label(mask):
    H, W = mask.shape
    lab = np.zeros((H, W), np.int32)
    comps = []
    cur = 0
    for y0 in range(H):
        for x0 in range(W):
            if mask[y0, x0] and lab[y0, x0] == 0:
                cur += 1
                q = deque([(y0, x0)])
                lab[y0, x0] = cur
                n = 0
                ys = []
                xs = []
                while q:
                    y, x = q.popleft()
                    n += 1
                    ys.append(y)
                    xs.append(x)
                    for dy in (-1, 0, 1):
                        for dx in (-1, 0, 1):
                            if dy == 0 and dx == 0:
                                continue
                            ny, nx = y + dy, x + dx
                            if 0 <= ny < H and 0 <= nx < W and mask[ny, nx] and lab[ny, nx] == 0:
                                lab[ny, nx] = cur
                                q.append((ny, nx))
                comps.append({"id": cur, "n": n, "bbox": (min(xs), min(ys), max(xs), max(ys))})
    return lab, comps


def dilate(mask, r):
    img = Image.fromarray((mask * 255).astype(np.uint8))
    return np.array(img.filter(ImageFilter.MaxFilter(r * 2 + 1))) > 0


def dist_to_edge(mask):
    """mask 内每个像素到最近 mask 外像素的 4 邻域距离。"""
    H, W = mask.shape
    INF = 10 ** 9
    d = np.full((H, W), INF, np.int32)
    q = deque()
    for y, x in zip(*np.nonzero(mask)):
        if not (mask[max(y - 1, 0), x] and mask[min(y + 1, H - 1), x]
                and mask[y, max(x - 1, 0)] and mask[y, min(x + 1, W - 1)]):
            d[y, x] = 0
            q.append((y, x))
    while q:
        y, x = q.popleft()
        nd = d[y, x] + 1
        for dy, dx in ((-1, 0), (1, 0), (0, -1), (0, 1)):
            ny, nx = y + dy, x + dx
            if 0 <= ny < H and 0 <= nx < W and mask[ny, nx] and d[ny, nx] > nd:
                d[ny, nx] = nd
                q.append((ny, nx))
    return d


def lerp(c0, c1, t):
    return c0 + (c1 - c0) * t


def energy_color(u):
    """u = 到边缘距离 / 附近的局部半带宽，0 在边缘，1 在带中心。"""
    cols = np.array([
        [8, 22, 115],
        [0, 105, 255],
        [0, 222, 255],
        [190, 250, 255],
        [246, 255, 255],
    ], np.float32)
    stops = np.array([0.0, 0.45, 0.72, 0.92, 1.0], np.float32)
    t = np.clip(u, 0, 1).astype(np.float32)
    out = np.zeros(u.shape + (3,), np.float32)
    for i in range(len(stops) - 1):
        lo, hi = stops[i], stops[i + 1]
        m = (t >= lo) & (t <= hi)
        if not m.any():
            continue
        f = ((t[m] - lo) / (hi - lo))[:, None]
        out[m] = cols[i] + (cols[i + 1] - cols[i]) * f
    return out


def smooth_noise(h, w, factor, blur, seed):
    small = np.random.default_rng(seed).random((max(2, h // factor), max(2, w // factor))).astype(np.float32)
    img = Image.fromarray((small * 255).astype(np.uint8)).resize((w, h), Image.BILINEAR)
    img = img.filter(ImageFilter.GaussianBlur(blur))
    arr = np.array(img, np.float32) / 255.0
    return (arr - arr.mean()) / max(arr.std(), 1e-4)


def main():
    rgba = np.array(Image.open(SRC).convert("RGBA"))
    alpha = rgba[..., 3]
    base = rgba[..., :3].astype(np.float32) / 255.0
    H, W = base.shape[:2]

    h, s, v = hsv_arrays(base)
    yellow = (h > 48) & (h < 70) & (s > 0.45) & (v > 0.5)
    orange = (h > 5) & (h < 30) & (s > 0.45) & (v > 0.1) & (v < 0.9)

    ylab, ycomps = label(yellow)
    keep_yellow_ids = {c["id"] for c in ycomps
                       if c["n"] >= 2 and c["bbox"][1] >= 425 and c["id"] not in (25, 26)}
    ykeep = np.isin(ylab, list(keep_yellow_ids))
    # 右侧细速度线
    ystreak = (ylab == 5)

    olab, ocomps = label(orange)
    near_ribbon = dilate(ykeep | ystreak, 3)
    okeep = np.zeros_like(orange)
    for c in ocomps:
        x0, y0, x1, y1 = c["bbox"]
        touches = np.any((olab == c["id"]) & near_ribbon)
        thin_streak = c["n"] <= 400 and x0 >= 500 and 400 <= y0 <= 500
        if touches or thin_streak:
            okeep |= olab == c["id"]

    core = ykeep | ystreak
    under = okeep & ~core
    mask = core | under

    # 轻微闭运算补掉边缘针孔
    mask_img = Image.fromarray((mask * 255).astype(np.uint8))
    mask = np.array(mask_img.filter(ImageFilter.MaxFilter(3)).filter(ImageFilter.MinFilter(3))) > 0
    mask = dilate(mask, 2)
    core = core & mask

    # ---- 黄色内核：按到中心线（骨架）的距离做蓝白渐变 ----
    # ---- 黄色内核：按局部带宽归一化的蓝白渐变 ----
    d_edge = dist_to_edge(core).astype(np.float32)
    d8s = np.clip(d_edge * 2, 0, 255).astype(np.uint8)
    d_smooth = np.array(Image.fromarray(d8s).filter(ImageFilter.GaussianBlur(0.8)), np.float32) / 2.0
    # 模糊会跨过窄缝把对侧大距离渗到边缘，造成白描边，这里压回真实距离附近
    d_smooth = np.minimum(d_smooth, d_edge + 1.0)
    d_core = d_edge.copy()
    d_core[~core] = 0
    d8 = np.clip(d_core * 2, 0, 255).astype(np.uint8)
    hw = np.array(Image.fromarray(d8).filter(ImageFilter.MaxFilter(41)), np.float32) / 2.0
    u = d_smooth / np.maximum(hw, 1.0)
    color = energy_color(u) / 255.0
    # 细飘带只有青色高光，避免整条刷白像白描边
    thin = np.clip((hw - 3.0) / 9.0, 0, 1)
    thin = thin * thin * (3 - 2 * thin)
    color = np.array([0.0, 0.88, 1.0], np.float32) * (1 - thin[..., None]) + color * thin[..., None]
    color[~core] = np.array([0.04, 0.11, 0.47], np.float32)

    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    wave = 0.5 + 0.5 * np.sin(xx * 0.045 + 2.0 * np.sin(yy * 0.012))
    noise = smooth_noise(H, W, 40, 2, 7)
    core_w = np.clip((u - 0.55) / 0.30, 0, 1)
    core_w = core_w * core_w * (3 - 2 * core_w)
    mod = 0.95 + 0.08 * wave + 0.02 * np.clip(noise, -1.5, 1.5)
    color *= ((1 - core_w) * mod + core_w)[..., None]

    # ---- 橙红底面：保留原有明暗，映射成深蓝 ----
    lum = (0.299 * base[..., 0] + 0.587 * base[..., 1] + 0.114 * base[..., 2])
    lum = np.clip((lum - 0.15) / 0.6, 0, 1)
    under_color = lerp(np.array([0.02, 0.05, 0.28], np.float32),
                       np.array([0.05, 0.32, 0.95], np.float32),
                       (lum ** 0.8)[..., None])
    color = np.where(under[..., None], under_color, color)

    # ---- 合成（1.5px 羽化边缘）----
    a = np.array(Image.fromarray((mask * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(2.2)), np.float32) / 255.0
    a = np.clip(a * 1.15, 0, 1)[..., None]
    out = base * (1 - a) + color * a

    # ---- 外发光 ----
    glow_small = np.array(Image.fromarray((mask * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(6)), np.float32) / 255.0
    glow_big = np.array(Image.fromarray((mask * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(26)), np.float32) / 255.0
    glow = np.clip(glow_small * 0.6 + glow_big * 0.55, 0, 1)[..., None]
    glow_rgb = np.array([0.0, 0.66, 1.0], np.float32)
    out = 1 - (1 - out) * (1 - glow * glow_rgb * 0.65)

    out = np.clip(out, 0, 1)

    rgb8 = (out * 255).astype(np.uint8)
    Image.fromarray(np.dstack([rgb8, alpha])).save(OUT)
    print(OUT)


if __name__ == "__main__":
    main()
