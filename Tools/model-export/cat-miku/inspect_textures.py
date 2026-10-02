import pathlib,struct,json,io
from PIL import Image,ImageOps,ImageDraw
root=pathlib.Path('Assets/Models/cat-hatsune-miku')
b=next((root/'source').glob('*.glb')).read_bytes(); n=struct.unpack_from('<I',b,12)[0]; d=json.loads(b[20:20+n]); off=28+n
items=[]
for i in d['images']:
 v=d['bufferViews'][i['bufferView']]; im=Image.open(io.BytesIO(b[off+v.get('byteOffset',0):off+v.get('byteOffset',0)+v['byteLength']])).convert('RGB'); items.append(('GLB '+i['name'],im))
for p in (root/'textures').glob('*.png'): items.append((p.name,Image.open(p).convert('RGB')))
out=Image.new('RGB',(1200,640),'#888888'); draw=ImageDraw.Draw(out)
for j,(name,im) in enumerate(items):
 x=j%6*200;y=j//6*320; out.paste(ImageOps.contain(im,(196,290)),(x,y+25));draw.text((x+4,y+5),name,fill='white')
out.save('Tools/model-export/cat-miku/texture-comparison.png')
