from pathlib import Path
import sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter
root = Path(__file__).resolve().parent
font = root.parent / "Fonts" / "NanoDotSong.ttf"
for name, text, size, width, height in [("SettingsTitle","设置",72,440,100),("DeathTitle","你倒下了……",76,540,110),("LevelSelect","关卡选择",52,480,80),("Thanks","致谢",52,480,80),("LevelSelectTitle","关卡选择//select",76,800,110),("ThanksTitle","致谢//thanks",76,800,110),("Title","临界行者",132,900,190),("Subtitle","// SuperQuicker",42,620,72),("PauseTitle","暂停//pause",76,540,110),("Retry","重新开始",52,480,80),("PauseExit","退出",52,480,80),("Start","开始游戏",52,480,80),("Continue","继续游戏",52,480,80),("Settings","设置",52,480,80),("Exit","退出游戏",52,480,80),("Close","返回",52,480,80),("Volume","主音量",52,480,80),("Fullscreen","切换全屏",52,480,80)]:
    if len(sys.argv) > 1 and name not in sys.argv[1:]:
        continue
    image = Image.new("RGBA", (width,height))
    draw = ImageDraw.Draw(image)
    face = ImageFont.truetype(str(font),size)
    box = draw.textbbox((0,0), text, font=face)
    draw.text(((width-box[2]-box[0])/2,(height-box[3]-box[1])/2),text,font=face,fill="white")
    if name not in ("SettingsTitle", "DeathTitle", "Title", "Subtitle", "PauseTitle", "Retry", "PauseExit", "LevelSelectTitle", "ThanksTitle"):
        # 用整数像素扩张保留点阵边缘，低透明度光晕避免形成硬描边。
        mask = image.getchannel("A")
        outer = Image.new("RGBA", image.size, (100, 22, 30, 0))
        outer.putalpha(mask.filter(ImageFilter.MaxFilter(5)).point(lambda value: value * 0.10))
        inner = Image.new("RGBA", image.size, (170, 65, 72, 0))
        inner.putalpha(mask.filter(ImageFilter.MaxFilter(3)).point(lambda value: value * 0.20))
        image = Image.alpha_composite(Image.alpha_composite(outer, inner), image)
    image.save(root / (name+".png"))
