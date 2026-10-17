"""用 Blender 原骨架制作挥击；动作参数和关键姿态集中保留，方便美术继续调整。"""
import bpy
import json
import math
from pathlib import Path
from mathutils import Vector, Quaternion

ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent
OUTPUT = ROOT / 'Assets/Animations/EnemyHeavy'
FPS = 30
SWING = 'HeavyHorizontalSwing'
RECOVER = 'HeavySwingRecover'

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=str(ROOT / 'Assets/Art/Models/EnemyHeavy/combat-mech_idle.fbx'))
scene = bpy.context.scene
scene.render.fps = FPS
scene.render.fps_base = 1
scene.frame_set(1)
rig = next(o for o in scene.objects if o.type == 'ARMATURE')
mesh = next(o for o in scene.objects if o.type == 'MESH')
base = {b.name: b.matrix_basis.copy() for b in rig.pose.bones}
rig.animation_data_clear()
for action in list(bpy.data.actions):
    bpy.data.actions.remove(action)
for bone in rig.pose.bones:
    bone.rotation_mode = 'QUATERNION'
    bone.matrix_basis = base[bone.name]
bpy.context.view_layer.update()
original = {b.name: b.matrix.copy() for b in rig.pose.bones}


def reset_pose():
    for bone in rig.pose.bones:
        bone.matrix_basis = base[bone.name]
    bpy.context.view_layer.update()


def rotate_world(name, degrees, axis):
    bone = rig.pose.bones[name]
    matrix = bone.matrix.copy()
    rotation = Quaternion(Vector(axis), math.radians(degrees)).to_matrix().to_4x4()
    position = matrix.translation.copy()
    matrix = rotation @ matrix
    matrix.translation = position
    bone.matrix = matrix
    bpy.context.view_layer.update()


def point_bone(name, direction):
    bone = rig.pose.bones[name]
    matrix = bone.matrix.copy()
    current = (bone.tail - bone.head).normalized()
    delta = current.rotation_difference(direction.normalized())
    rotated = delta.to_matrix().to_4x4() @ matrix
    rotated.translation = matrix.translation
    bone.matrix = rotated
    bpy.context.view_layer.update()


def solve_arm(side, wrist_offset, elbow_hint):
    upper = rig.pose.bones['arm_' + side]
    lower = rig.pose.bones['arm_' + side + '.001']
    shoulder = upper.head.copy()
    wrist = shoulder + Vector(wrist_offset)
    length_a = (upper.tail - upper.head).length
    length_b = (lower.tail - lower.head).length
    toward = wrist - shoulder
    distance = min(toward.length, length_a + length_b - .015)
    toward.normalize()
    projection = (length_a ** 2 - length_b ** 2 + distance ** 2) / (2 * distance)
    height = math.sqrt(max(0, length_a ** 2 - projection ** 2))
    hint = Vector(elbow_hint)
    perpendicular = (hint - toward * hint.dot(toward)).normalized()
    elbow = shoulder + toward * projection + perpendicular * height
    point_bone(upper.name, elbow - shoulder)
    point_bone(lower.name, wrist - lower.head)


# 时间以秒计；挥击末帧同时是收回首帧，关键姿态只定义一次。
# 右臂的坐标以肩为原点：-X 向身体右侧、+Y 向后、Z 向上。
POSES = [
    (0.00, 0, 0, 0, (-.05, -3.08, -5.81), (-1, -.25, 0)),
    (.40, -5, -1, 1, (-1.1, -2.6, -4.5), (-2, .2, 0)),
    (1.15, -19, -3, 2, (-2.6, .9, -2.0), (-2, -.2, .4)),
    (1.90, -28, -4, 3, (-2.5, 3.8, -1.5), (-2, -.3, .6)),
    (2.12, -30, -4, 3, (-2.6, 4.0, -1.5), (-2, -.3, .6)),
    (2.34, -4, 2, -1, (-1.8, -4.4, -1.9), (-2, .2, .4)),
    (2.54, 26, 4, -3, (3.2, -4.2, -2.1), (-1.5, -1, .6)),
    (2.75, 30, 3, -2, (3.8, -3.5, -2.3), (-1.5, -1, .5)),
    (3.00, 27, 2, -1, (3.5, -3.7, -2.2), (-1.5, -1, .5)),
    (3.35, 24, 1, -1, (3.2, -3.4, -2.7), (-1.5, -.5, .3)),
    (3.85, 12, 0, 0, (1.1, -2.9, -4.0), (-1.2, -.2, .1)),
    (4.45, 3, 0, 0, (-.05, -2.8, -5.35), (-1, -.25, 0)),
    (5.00, 0, 0, 0, (-.05, -3.08, -5.81), (-1, -.25, 0)),
]


def pose(values):
    time, yaw, lean, roll, wrist, hint = values
    reset_pose()
    if time == 0 or time == 5:
        return
    rotate_world('body', yaw, (0, 0, 1))
    rotate_world('body', lean, (1, 0, 0))
    rotate_world('body', roll, (0, 1, 0))
    # 肩膀跟随躯干，脚掌与根节点保留待机姿态，避免原地攻击产生滑步。
    rotate_world('head', -yaw * .22, (0, 0, 1))
    solve_arm('R', wrist, hint)
    # 左臂轻微提起制衡；机械手指随挥击握紧。
    strength = min(time / 1.2, 1, max(0, (5 - time) / 1.1))
    rotate_world('arm_L', 12 * strength, (1, 0, 0))
    rotate_world('arm_L.001', 15 * strength, (1, 0, 0))
    for name in ('finger_R', 'finger_R.001'):
        rotate_world(name, 18 * strength, (1, 0, 0))


master = bpy.data.actions.new('HeavyMelee_5s_EDIT')
master.use_fake_user = True
rig.animation_data_create()
rig.animation_data.action = master
for values in POSES:
    frame = 1 + round(values[0] * FPS)
    pose(values)
    for bone in rig.pose.bones:
        bone.keyframe_insert('location', frame=frame, group=bone.name)
        bone.keyframe_insert('rotation_quaternion', frame=frame, group=bone.name)
        bone.keyframe_insert('scale', frame=frame, group=bone.name)
for curve in master.fcurves:
    for key in curve.keyframe_points:
        key.interpolation = 'BEZIER'
        key.handle_left_type = 'AUTO_CLAMPED'
        key.handle_right_type = 'AUTO_CLAMPED'


def baked_action(name, start, end):
    samples = []
    rig.animation_data.action = master
    for frame in range(start, end + 1):
        scene.frame_set(frame)
        samples.append({b.name: (b.location.copy(), b.rotation_quaternion.copy(), b.scale.copy()) for b in rig.pose.bones})
    action = bpy.data.actions.new(name)
    action.use_fake_user = True
    rig.animation_data.action = action
    previous = {}
    for frame, sample in enumerate(samples, 1):
        for bone in rig.pose.bones:
            location, rotation, scale = sample[bone.name]
            if bone.name in previous and previous[bone.name].dot(rotation) < 0:
                rotation.negate()
            previous[bone.name] = rotation.copy()
            bone.location, bone.rotation_quaternion, bone.scale = location, rotation, scale
            bone.keyframe_insert('location', frame=frame, group=bone.name)
            bone.keyframe_insert('rotation_quaternion', frame=frame, group=bone.name)
            bone.keyframe_insert('scale', frame=frame, group=bone.name)
    for curve in action.fcurves:
        for key in curve.keyframe_points:
            key.interpolation = 'LINEAR'
    return action


swing = baked_action(SWING, 1, 91)
recover = baked_action(RECOVER, 91, 151)
OUTPUT.mkdir(exist_ok=True)
for action, duration in ((swing, 3), (recover, 2)):
    rig.animation_data.action = action
    scene.frame_start, scene.frame_end = 1, 1 + duration * FPS
    scene.frame_set(1)
    bpy.ops.object.select_all(action='DESELECT')
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    # 只导骨架和一个活动动作，保持原有层级；不导出多余叶骨或网格副本。
    bpy.ops.export_scene.fbx(
        filepath=str(OUTPUT / (action.name + '.fbx')),
        use_selection=True, object_types={'ARMATURE'}, add_leaf_bones=False,
        axis_forward='-Z', axis_up='Y', apply_unit_scale=True,
        bake_anim=True, bake_anim_use_all_bones=True,
        bake_anim_use_nla_strips=False, bake_anim_use_all_actions=False,
        bake_anim_force_startend_keying=True, bake_anim_step=1,
        bake_anim_simplify_factor=0,
    )

# .blend 保留模型、稀疏可编辑关键帧和两个已烘焙动作；不放 Assets，避免 Unity 自动调用 Blender。
rig.animation_data.action = master
scene.frame_start, scene.frame_end = 1, 151
scene.timeline_markers.clear()
for label, frame in [('READY', 1), ('PULL_BACK', 35), ('CHARGED', 58), ('SWEEP', 71), ('FOLLOW_THROUGH', 83), ('RECOVER_START', 91), ('IDLE', 151)]:
    scene.timeline_markers.new(label, frame=frame)

# 预览只替换 Blender 的材质，不影响 Unity 原模型或材质。
material = bpy.data.materials.new('HeavyPreview')
material.use_nodes = True
bsdf = material.node_tree.nodes.get('Principled BSDF')
bsdf.inputs['Base Color'].default_value = (.30, .36, .40, 1)
bsdf.inputs['Metallic'].default_value = .55
bsdf.inputs['Roughness'].default_value = .4
texture = ROOT / 'Assets/Art/Models/EnemyHeavy/textures/Mech_baseColor.jpg'
if texture.exists():
    node = material.node_tree.nodes.new('ShaderNodeTexImage')
    node.image = bpy.data.images.load(str(texture))
    material.node_tree.links.new(node.outputs['Color'], bsdf.inputs['Base Color'])
mesh.data.materials.clear()
mesh.data.materials.append(material)
for poly in mesh.data.polygons:
    poly.material_index = 0

scene.world = bpy.data.worlds.new('HeavyPreviewWorld')
scene.world.color = (.15, .15, .15)
for name, location, energy, size in [
    ('Key', (-8, -12, 17), 3200, 9),
    ('Fill', (10, -7, 11), 2200, 8),
    ('Rim', (-2, 10, 15), 3500, 7),
]:
    data = bpy.data.lights.new(name, 'AREA')
    data.energy, data.shape, data.size = energy, 'DISK', size
    obj = bpy.data.objects.new(name, data)
    scene.collection.objects.link(obj)
    obj.location = location
    obj.rotation_euler = (Vector((0, 0, 5)) - obj.location).to_track_quat('-Z', 'Y').to_euler()
camera_data = bpy.data.cameras.new('PreviewCamera')
camera = bpy.data.objects.new('PreviewCamera', camera_data)
scene.collection.objects.link(camera)
scene.camera = camera
camera.location = (-17, -24, 14)
camera.rotation_euler = (Vector((0, -.2, 5.2)) - camera.location).to_track_quat('-Z', 'Y').to_euler()
camera_data.type = 'ORTHO'
camera_data.ortho_scale = 22
scene.render.engine = 'BLENDER_EEVEE_NEXT'
scene.eevee.taa_render_samples = 24
scene.render.resolution_x = 640
scene.render.resolution_y = 600
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = 'PNG'
scene.view_settings.view_transform = 'AgX'
scene.frame_set(1)
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type == 'VIEW_3D':
            area.spaces.active.region_3d.view_distance = 21
            area.spaces.active.region_3d.view_location = (0, 0, 5)
            area.spaces.active.region_3d.view_rotation = camera.rotation_euler.to_quaternion()
bpy.ops.wm.save_as_mainfile(filepath=str(HERE / 'EnemyHeavy_Melee.blend'))

report = {'fps': FPS, 'actions': {SWING: 3, RECOVER: 2}, 'bone_count': len(rig.pose.bones), 'poses': []}
for frame in [1, 35, 58, 65, 71, 77, 83, 91, 106, 121, 136, 151]:
    scene.frame_set(frame)
    report['poses'].append({'frame': frame, 'wrist_R': list(rig.pose.bones['hand_R'].head), 'foot_R': list(rig.pose.bones['foot_r'].head), 'foot_L': list(rig.pose.bones['foot_L'].head)})
    scene.render.filepath = str(HERE / ('pose_%03d.png' % frame))
    bpy.ops.render.render(write_still=True)
(HERE / 'animation-report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print('HEAVY_MELEE_AUTHORING_COMPLETE')
