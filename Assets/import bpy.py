import bpy

print("== 模型变换信息 ==")

for obj in bpy.data.objects:
    if obj.type == 'MESH':  # 只处理网格模型
        name = obj.name
        location = obj.location
        scale = obj.scale

        print(f"对象: {name}")
        print(f"  位置: X={location.x:.3f}, Y={location.y:.3f}, Z={location.z:.3f}")
        print(f"  缩放: X={scale.x:.3f}, Y={scale.y:.3f}, Z={scale.z:.3f}")
