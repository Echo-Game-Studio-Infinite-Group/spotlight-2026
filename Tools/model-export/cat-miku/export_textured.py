import bpy, json
from pathlib import Path
from mathutils import Vector
ROOT = Path(__file__).resolve().parents[3]
ASSET = ROOT / 'Assets/Models/cat-hatsune-miku'
OUT = ASSET / 'source/cat-hatsune-miku-textured.fbx'
REPORT = Path(__file__).resolve().parent
MAPPING = {'Material.003': ('Cloth_Accessories', 'cloth_acc.png'), 'Material.002': ('Face_Skin', 'Untitled.001_1.png'), 'Material': ('Body_Skin', 'skin.png'), 'pc': ('Cloth', 'cloth.png'), 'dec': ('Earphone', 'earphone.png'), 'Material.001': ('Hair', 'hair.png')}
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=str(next((ASSET / 'source').glob('*.glb'))))
meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
arms = [o for o in bpy.context.scene.objects if o.type == 'ARMATURE']
# glTF 导入器生成的骨骼控制球不是源模型几何，不参与导出验收。
custom_shapes = {bone.custom_shape for arm in arms for bone in arm.pose.bones if bone.custom_shape}
meshes = [o for o in meshes if o not in custom_shapes]
source = {'meshes': len(meshes), 'vertices': sum(len(o.data.vertices) for o in meshes), 'bones': sum(len(o.data.bones) for o in arms), 'actions': [a.name for a in bpy.data.actions]}
for obj in meshes:
    for slot in obj.material_slots:
        if slot.material and slot.material.name == 'pc.001':
            slot.material = bpy.data.materials['pc']
used = {s.material for o in meshes for s in o.material_slots if s.material}
assert {m.name for m in used} == set(MAPPING), [m.name for m in used]
for mat in used:
    name, filename = MAPPING[mat.name]
    mat.name = name
    mat.use_nodes = True
    mat.node_tree.nodes.clear()
    output = mat.node_tree.nodes.new('ShaderNodeOutputMaterial')
    shader = mat.node_tree.nodes.new('ShaderNodeBsdfPrincipled')
    shader.inputs['Roughness'].default_value = 0.9
    shader.inputs['Metallic'].default_value = 0
    shader.inputs['Specular IOR Level'].default_value = 0
    texture = mat.node_tree.nodes.new('ShaderNodeTexImage')
    texture.image = bpy.data.images.load(str(ASSET / 'textures' / filename), check_existing=True)
    texture.image.colorspace_settings.name = 'sRGB'
    mat.node_tree.links.new(texture.outputs['Color'], shader.inputs['Base Color'])
    mat.node_tree.links.new(shader.outputs['BSDF'], output.inputs['Surface'])
    mat.use_backface_culling = False
# 外部图片与内嵌图片上下翻转，直接修正 UV，避免依赖 FBX 不兼容的节点映射。
for mesh in {o.data for o in meshes}:
    for layer in mesh.uv_layers:
        for loop in layer.data:
            loop.uv.y = 1.0 - loop.uv.y
bpy.ops.object.select_all(action='DESELECT')
for obj in meshes + arms:
    obj.hide_set(False)
    obj.hide_viewport = False
    obj.select_set(True)
bpy.ops.export_scene.fbx(filepath=str(OUT), use_selection=True, object_types={'MESH','ARMATURE'}, axis_forward='-Z', axis_up='Y', add_leaf_bones=False, path_mode='COPY', embed_textures=True, bake_anim=True, bake_anim_use_all_actions=True, bake_anim_use_nla_strips=False)
# 从落盘 FBX 重新导入验收，验证骨骼、网格、动画及贴图连接。
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=str(OUT))
meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
arms = [o for o in bpy.context.scene.objects if o.type == 'ARMATURE']
materials = {s.material for o in meshes for s in o.material_slots if s.material}
textures = {m.name: [Path(bpy.path.abspath(n.image.filepath)).name for n in m.node_tree.nodes if n.type == 'TEX_IMAGE' and n.image] for m in materials}
result = {'meshes':len(meshes), 'vertices':sum(len(o.data.vertices) for o in meshes), 'bones':sum(len(o.data.bones) for o in arms), 'actions':[a.name for a in bpy.data.actions], 'textures':textures}
print('VALIDATION', source, result)
assert source['meshes'] == result['meshes']
assert source['vertices'] == result['vertices']
assert source['bones'] == result['bones']
assert len(source['actions']) == len(result['actions'])
assert len(textures) == 6 and all(textures.values())
(REPORT/'export-report.json').write_text(json.dumps({'source':source,'exported':result,'fbx':str(OUT)}, ensure_ascii=False,indent=2),encoding='utf-8')
# 预览使用静止绑定姿势，不更改导出的动画数据。
for arm in arms:
    arm.data.pose_position = 'REST'
bpy.context.view_layer.update()
points = [o.matrix_world @ Vector(c) for o in meshes for c in o.bound_box]
low = Vector(tuple(min(p[i] for p in points) for i in range(3)))
high = Vector(tuple(max(p[i] for p in points) for i in range(3)))
center = (low+high)/2
size = max(high-low)
bpy.ops.object.camera_add(location=center+Vector((0,-size*2,0)))
camera=bpy.context.object
camera.rotation_euler=(center-camera.location).to_track_quat('-Z','Y').to_euler()
camera.data.type='ORTHO'; camera.data.ortho_scale=size*1.15
scene=bpy.context.scene; scene.camera=camera
scene.render.engine='CYCLES'; scene.cycles.samples=16
scene.world=bpy.data.worlds.new('PreviewWorld'); scene.world.use_nodes=True
scene.world.node_tree.nodes['Background'].inputs[0].default_value=(0.65,0.65,0.65,1)
scene.world.node_tree.nodes['Background'].inputs[1].default_value=0.8
bpy.ops.object.light_add(type='AREA',location=center+Vector((size,-size,size)))
bpy.context.object.data.energy=500; bpy.context.object.data.shape='DISK'; bpy.context.object.data.size=size
bpy.context.object.rotation_euler=(center-bpy.context.object.location).to_track_quat('-Z','Y').to_euler()
scene.render.resolution_x=900; scene.render.resolution_y=1000; scene.render.resolution_percentage=100
scene.view_settings.view_transform='Standard'
scene.render.filepath=str(REPORT/'textured-preview.png')
bpy.ops.render.render(write_still=True)
print('EXPORT_VALIDATED',json.dumps(result,ensure_ascii=False))



