# Builds the MSIX tile and Store logos from the app icon (src/IsleBar.App/Assets/islebar.ico). Run when the icon changes.
import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ICO = os.path.join(HERE, '..', 'src', 'IsleBar.App', 'Assets', 'islebar.ico')
OUT = os.path.join(HERE, 'Assets')
os.makedirs(OUT, exist_ok=True)

ico = Image.open(ICO)
ico.size = max(ico.info.get('sizes', [ico.size]))   # the 256 px frame
icon = ico.convert('RGBA')

def tile(w, h, fill):
    """The icon centred on a transparent tile, taking `fill` of the shorter side (tile guidelines leave a margin)."""
    canvas = Image.new('RGBA', (w, h), (0, 0, 0, 0))
    side = int(min(w, h) * fill)
    canvas.alpha_composite(icon.resize((side, side), Image.LANCZOS), ((w - side) // 2, (h - side) // 2))
    return canvas

for name, w, h, fill in [('Square44x44Logo.png', 44, 44, 1.0), ('Square150x150Logo.png', 150, 150, 0.66),
                         ('Wide310x150Logo.png', 310, 150, 0.66), ('StoreLogo.png', 50, 50, 1.0),
                         ('StoreLogo-300.png', 300, 300, 0.9)]:
    tile(w, h, fill).save(os.path.join(OUT, name))
    print(name)
