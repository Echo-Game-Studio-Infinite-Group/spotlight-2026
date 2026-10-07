// 落地点查询策略。独立成文件而不是塞在 Spawner 里：
// 枚举要被 Spawner 的 Inspector、SpawnGroundQuery 的签名、以及测试同时引用，
// 放在自己文件里可以避免"测试为了拿一个枚举去引用整个 Spawner"。
public enum SpawnGroundMode
{
    /// <summary>先 NavMesh 采样，失败再向下射线。推荐值：兼顾分层正确性与未烘焙时的可用性。</summary>
    NavMeshThenRaycast = 0,

    /// <summary>只用 NavMesh。分层地块最准确；未烘焙 NavMesh 时全部生成失败（会打警告）。</summary>
    NavMeshOnly = 1,

    /// <summary>只用向下射线。不依赖烘焙，但上下层重叠时会打到最上面那块。</summary>
    PhysicsRaycastOnly = 2
}
