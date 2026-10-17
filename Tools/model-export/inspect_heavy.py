import bpy
import json
from pathlib import Path

root = Path(__file__).resolve().parents[2]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=str(root / 'Assets/Art/Models/EnemyHeavy/combat-mech_idle.fbx'))
scene = bpy.context.scene
scene.frame_set(0)
report = {'objects': [], 'bones': [], 'actions': []}
for obj in scene.objects:
    report['objects'].append({'name': obj.name, 'type': obj.type, 'parent': obj.parent.name if obj.parent else None, 'location': list(obj.location), 'rotation': list(obj.rotation_euler), 'scale': list(obj.scale), 'dimensions': list(obj.dimensions)})
    if obj.type == 'ARMATURE':
        for bone in obj.pose.bones:
            report['bones'].append({'name': bone.name, 'parent': bone.parent.name if bone.parent else None, 'head': list(obj.matrix_world @ bone.head), 'tail': list(obj.matrix_world @ bone.tail), 'rotation': list(bone.rotation_quaternion), 'basis': [list(r) for r in bone.bone.matrix_local.to_3x3()]})
for action in bpy.data.actions:
    report['actions'].append({'name': action.name, 'range': list(action.frame_range)})
out = root / 'Tools/model-export/enemy-heavy'
out.mkdir(exist_ok=True)
(out / 'rig-inspection.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report, indent=2))
