using UnityEngine;

// 可见胶囊跟随受击框：PlayerMotor 每 tick 按速度改写 CharacterController 的 height/radius（受击框变细 + 滑铲压低），
// 视觉体若不跟随，灰盒上会出现"看着擦过去了、判定其实撞上"的误判——高速变细是本作可读性的核心反馈
// 同步放 LateUpdate：本帧 FixedUpdate 的胶囊尺寸已结算完，渲染帧只做一次搬运
[RequireComponent(typeof(CharacterController))]
public class CapsuleVisualSync : MonoBehaviour
{
    [SerializeField] private Transform _visual;

    // Unity 内置胶囊图元的基准尺寸（高 2 / 直径 1），缩放换算是唯一硬编码处
    private const float PrimitiveHeight = 2f;
    private const float PrimitiveDiameter = 1f;

    private CharacterController _controller;

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        if (_visual == null)
        {
            Debug.LogError("[CapsuleVisualSync] _visual 未赋值，可见胶囊不会跟随受击框", this);
            enabled = false;
        }
    }

    private void LateUpdate()
    {
        // 图元 pivot 在几何中心，而 CharacterController.center 已表达"底面贴脚底"的约定，直接复用不重复推导
        _visual.localPosition = _controller.center;
        _visual.localScale = new Vector3(
            _controller.radius * 2f / PrimitiveDiameter,
            _controller.height / PrimitiveHeight,
            _controller.radius * 2f / PrimitiveDiameter);
    }
}
