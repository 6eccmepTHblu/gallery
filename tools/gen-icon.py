"""Иконка приложения (SPEC.md §7.5, п. «Иконка и имя приложения»).

Запуск:  python tools/gen-icon.py
Пишет:   src/Assets/app.ico  — размеры 256/48/32/16

Замысел: приложение — это кадр, который одним движением уходит в папку.
Поэтому глиф — кадр со стрелкой вправо. На 16 px от кадра остаётся рамка,
а от стрелки — узнаваемый треугольник; сложнее рисовать нельзя, развалится.
"""
import os
import sys

from PIL import Image, ImageDraw

S = 1024                      # рисуем крупно, уменьшаем с LANCZOS
OUT_SIZES = [256, 48, 32, 16]

ACCENT_TOP = (76, 194, 255)   # #4CC2FF — акцент приложения
ACCENT_BOT = (26, 118, 200)
WHITE = (255, 255, 255, 255)


def rounded_gradient(size, radius, top, bot):
    """Скруглённый квадрат с вертикальным градиентом."""
    grad = Image.new("RGB", (1, size))
    for y in range(size):
        t = y / (size - 1)
        grad.putpixel((0, y), tuple(int(top[i] + (bot[i] - top[i]) * t) for i in range(3)))
    grad = grad.resize((size, size))

    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)

    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    img.paste(grad, (0, 0), mask)
    return img


def draw_glyph(img):
    d = ImageDraw.Draw(img)
    m = S // 8                                  # поля

    # Рамка кадра: толстая, чтобы пережить уменьшение до 16 px
    fx0, fy0 = m + S // 24, m + S // 8
    fx1, fy1 = S - m - S // 3, S - m - S // 8
    d.rounded_rectangle([fx0, fy0, fx1, fy1], radius=S // 22,
                        outline=WHITE, width=S // 18)

    # Внутри кадра — солнце и гора. На 16 px сольются в пятно, и это нормально:
    # силуэт рамки всё равно читается.
    w, h = fx1 - fx0, fy1 - fy0
    r = w // 9
    cx, cy = fx0 + w // 3, fy0 + h // 3
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=WHITE)
    d.polygon([(fx0 + w // 6, fy1 - h // 6),
               (fx0 + w // 2, fy0 + h // 2),
               (fx1 - w // 6, fy1 - h // 6)], fill=WHITE)

    # Стрелка «в папку» — то, чем это приложение отличается от просмотрщика
    ax = fx1 + S // 14
    ay = S // 2
    a = S // 9
    d.polygon([(ax, ay - a), (ax + a * 1.4, ay), (ax, ay + a)], fill=WHITE)
    d.rounded_rectangle([ax - a * 1.1, ay - a // 3, ax + a // 4, ay + a // 3],
                        radius=a // 3, fill=WHITE)


def main():
    big = rounded_gradient(S, S // 5, ACCENT_TOP, ACCENT_BOT)
    draw_glyph(big)

    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    assets = os.path.join(here, "src", "Assets")
    os.makedirs(assets, exist_ok=True)

    png = os.path.join(assets, "app-256.png")
    big.resize((256, 256), Image.LANCZOS).save(png)

    ico = os.path.join(assets, "app.ico")
    big.save(ico, format="ICO", sizes=[(s, s) for s in OUT_SIZES])

    print(f"{ico}  ({', '.join(str(s) for s in OUT_SIZES)})")
    print(f"{png}  (предпросмотр)")


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    main()
