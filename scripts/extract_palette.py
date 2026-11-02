"""Extract the dominant colours of the Juvo logo (Pillow) and print a report.

Usage: python -X utf8 scripts/extract_palette.py "Logo/Official Logo.jpg"
"""
import sys
from collections import Counter
from PIL import Image

def hexc(c): return "#{:02X}{:02X}{:02X}".format(*c)
def lum(c):
    def f(v):
        v/=255; return v/12.92 if v<=0.03928 else ((v+0.055)/1.055)**2.4
    r,g,b=map(f,c); return 0.2126*r+0.7152*g+0.0722*b
def contrast(a,b):
    la,lb=lum(a),lum(b); hi,lo=max(la,lb),min(la,lb); return (hi+0.05)/(lo+0.05)

img=Image.open(sys.argv[1]).convert("RGB")
w,h=img.size
small=img.resize((256,256))
q=small.quantize(colors=10,method=Image.Quantize.MEDIANCUT)
pal=q.getpalette(); counts=Counter(q.getdata())
print("dominant colours (share of pixels):")
for idx,n in counts.most_common():
    c=tuple(pal[idx*3:idx*3+3]); print(f"  {hexc(c)}  {n/(256*256):.1%}")
print("sampled regions (median of 9x9 block):")
pts={"background top-left":(0.1,0.1),"background bottom-right":(0.9,0.9),"ring (white)":(0.205,0.5),
     "inner disc (dark)":(0.30,0.30),"letter J (white)":(0.52,0.35),"swoosh (bright red)":(0.50,0.64)}
for name,(fx,fy) in pts.items():
    x,y=int(fx*w),int(fy*h)
    px=sorted(img.getpixel((min(w-1,x+dx),min(h-1,y+dy))) for dx in range(-4,5) for dy in range(-4,5))
    c=px[len(px)//2]; print(f"  {name:26s} {hexc(c)}  contrast on white {contrast(c,(255,255,255)):.2f}")
