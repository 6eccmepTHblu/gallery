"""Генератор тестового корпуса (SPEC.md §10.2).

Запуск:  python tools/gen-testdata.py [папка]
По умолчанию кладёт в ./testdata (в .gitignore — файлы генерируемые, в репозитории не нужны).
"""
import os
import random
import shutil
import struct
import sys

from PIL import Image, ImageDraw

OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "testdata")

# готовые 24 МП файлы с прошлых замеров — переиспользуем, если лежат рядом
LEGACY = (r"C:\Users\Slava\AppData\Local\Temp\claude"
          r"\D--Project-Personal-gallery\2a29ac28-4541-4c0d-a1cc-0b6464d880c4"
          r"\scratchpad\wpfprobe")


def noisy(w, h, seed=1):
    """Шумная картинка: JPEG на ней не сжимается, декод честно тяжёлый."""
    rnd = random.Random(seed)
    im = Image.new("RGB", (w, h))
    d = ImageDraw.Draw(im)
    for _ in range(w * h // 400):
        x, y = rnd.randrange(w), rnd.randrange(h)
        d.ellipse([x, y, x + rnd.randrange(3, 24), y + rnd.randrange(3, 24)],
                  fill=(rnd.randrange(256), rnd.randrange(256), rnd.randrange(256)))
    return im


def smooth(w, h):
    """Градиент: сжимается отлично, декод лёгкий."""
    im = Image.new("RGB", (w, h))
    d = ImageDraw.Draw(im)
    for y in range(0, h, 4):
        d.rectangle([0, y, w, y + 4], fill=(y * 255 // h, 90, 255 - y * 255 // h))
    return im


def portrait_marked(w, h):
    """Портрет с явным верхом — сразу видно, применён ли EXIF-поворот."""
    im = Image.new("RGB", (w, h), (24, 24, 28))
    d = ImageDraw.Draw(im)
    d.rectangle([0, 0, w, h // 6], fill=(60, 200, 140))
    d.text((w // 12, h // 14), "TOP", fill=(0, 0, 0))
    d.ellipse([w // 4, h // 2 - w // 4, w * 3 // 4, h // 2 + w // 4], fill=(200, 90, 70))
    return im


def save_jpeg(im, path, orientation=None, **kw):
    if orientation is not None:
        ex = im.getexif()
        ex[274] = orientation
        kw["exif"] = ex.tobytes()
    im.save(path, "JPEG", quality=90, **kw)


def main():
    os.makedirs(OUT, exist_ok=True)
    made = []

    def note(name):
        made.append((name, os.path.getsize(os.path.join(OUT, name))))

    # --- 24 МП: два профиля нагрузки. Переиспользуем готовые, иначе генерируем.
    for src, dst in (("test24mp.jpg", "01_baseline_24mp.jpg"),
                     ("test24mp_smooth.jpg", "02_smooth_24mp.jpg")):
        s, d = os.path.join(LEGACY, src), os.path.join(OUT, dst)
        if os.path.exists(s):
            shutil.copyfile(s, d)
        else:
            im = noisy(6000, 4000) if "smooth" not in src else smooth(6000, 4000)
            save_jpeg(im, d, orientation=6)
        note(dst)

    # --- прогрессивный JPEG: замерено, что WIC на нём теряет scaled decode (§6.3)
    save_jpeg(noisy(3840, 2160, seed=7), os.path.join(OUT, "03_progressive_8mp.jpg"),
              progressive=True)
    note("03_progressive_8mp.jpg")

    # --- ориентация: 6 = поворот на 90°, 5 = зеркало + поворот (падает почти у всех)
    save_jpeg(portrait_marked(1200, 1800), os.path.join(OUT, "04_orient6.jpg"), orientation=6)
    note("04_orient6.jpg")
    save_jpeg(portrait_marked(1200, 1800), os.path.join(OUT, "05_orient5_mirrored.jpg"), orientation=5)
    note("05_orient5_mirrored.jpg")
    save_jpeg(portrait_marked(1200, 1800), os.path.join(OUT, "06_orient1_reference.jpg"), orientation=1)
    note("06_orient1_reference.jpg")

    # --- PNG 24 МП: scaled decode невозможен в принципе, худший случай
    noisy(6000, 4000, seed=3).save(os.path.join(OUT, "07_png_24mp.png"), "PNG")
    note("07_png_24mp.png")

    # --- прозрачность: проверка шахматки
    a = Image.new("RGBA", (800, 600), (0, 0, 0, 0))
    ImageDraw.Draw(a).ellipse([100, 100, 700, 500], fill=(80, 200, 255, 210))
    a.save(os.path.join(OUT, "08_alpha.png"), "PNG")
    note("08_alpha.png")

    # --- патология
    Image.new("RGB", (1, 1), (255, 0, 0)).save(os.path.join(OUT, "09_tiny_1x1.png"))
    note("09_tiny_1x1.png")

    open(os.path.join(OUT, "10_zero.jpg"), "wb").close()
    note("10_zero.jpg")

    whole = open(os.path.join(OUT, "02_smooth_24mp.jpg"), "rb").read()
    open(os.path.join(OUT, "11_truncated.jpg"), "wb").write(whole[: len(whole) * 6 // 10])
    note("11_truncated.jpg")

    # PNG с расширением .jpg: WIC определяет по магическим байтам, а не по имени
    smooth(400, 300).save(os.path.join(OUT, "_tmp.png"), "PNG")
    os.replace(os.path.join(OUT, "_tmp.png"), os.path.join(OUT, "12_png_named.jpg"))
    note("12_png_named.jpg")

    # панорама шире лимита текстуры D3D (16384)
    save_jpeg(smooth(20000, 200), os.path.join(OUT, "13_pano_20000x200.jpg"))
    note("13_pano_20000x200.jpg")

    # --- натуральная сортировка: img_2 обязан идти перед img_10
    for n in (1, 2, 10, 20, 100):
        save_jpeg(smooth(320, 240), os.path.join(OUT, f"14_img_{n}.jpg"))
        note(f"14_img_{n}.jpg")

    # --- Unicode и bidi-спуфинг в имени
    save_jpeg(smooth(320, 240), os.path.join(OUT, "15_эмодзи_🎞_имя.jpg"))
    note("15_эмодзи_🎞_имя.jpg")
    save_jpeg(smooth(320, 240), os.path.join(OUT, "16_rlo_\u202egpj.jpg"))
    note("16_rlo_\u202egpj.jpg")

    print(f"{OUT}\n")
    for name, size in made:
        print(f"  {size:>12,}  {name}")
    print(f"\nвсего {len(made)} файлов")


if __name__ == "__main__":
    # консоль Windows по умолчанию cp1251 и давится эмодзи в именах файлов
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    main()
