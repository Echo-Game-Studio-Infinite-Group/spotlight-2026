using UnityEngine;

// RuinsFireLod —— 氛围火的"两档位 + 三档距离"裁剪（对应调研 §6.5 / §6.7）
//
// 本项目是跑酷路边火、一闪而过（20 m/s 量级），所以：
//   · 远处（超出预告视距）连火苗卡片都不必给，只留烘在图上的光斑；
//   · 中距（进入预告视距）给火苗 + 光晕；
//   · 近处/驻足点才补齐余烬、烟、闪烁灯这些"细货"。
// 这里把"细货"列成 detailObjects：超过 detailDistance 就整体关掉（省的是模拟与叠加成本），
// 超过 cullDistance 连主体一起关。消失不做硬切——形状淡出由 shader 的 _FadeStart/_FadeEnd
// 距离淡出兜底，所以 LOD 切换肉眼不可见（这正是把淡出写进 shader 的原因）。
[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class RuinsFireLod : MonoBehaviour
{
    [Tooltip("超过这个距离，关掉 detailObjects（余烬/烟/动态灯等只在近处/驻足档出现的层）")]
    public float detailDistance = 45f;

    [Tooltip("超过这个距离，整堆火关闭")]
    public float cullDistance = 130f;

    [Tooltip("只在近处/驻足档才出现的子物体（余烬、烟、点光等）")]
    public GameObject[] detailObjects = new GameObject[0];

    [Tooltip("勾上后本组件只在运行时生效（编辑器里始终全开，方便布点与出图）")]
    public bool runtimeOnly = true;

    void OnEnable() => Apply();
    void Update() => Apply();

    public void Apply()
    {
        if (runtimeOnly && !Application.isPlaying) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        float d = Vector3.Distance(cam.transform.position, transform.position);
        bool detail = d <= detailDistance;
        bool alive = d <= cullDistance;

        for (int i = 0; i < detailObjects.Length; i++)
        {
            if (detailObjects[i] != null && detailObjects[i].activeSelf != detail)
                detailObjects[i].SetActive(detail);
        }

        if (gameObject.activeSelf != alive) gameObject.SetActive(alive);
    }
}
