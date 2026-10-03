using UnityEngine;

// 使用配置好的粒子，不用程序生成替代特效。
[DisallowMultipleComponent]
public sealed class PlayerVFXManager : MonoBehaviour
{
    [SerializeField] private Transform _origin;
    public ParticleSystem attack1;
    public ParticleSystem attack2;
    public ParticleSystem attack3;

    public void Configure(Transform origin) => _origin = origin;

    private void OnDestroy()
    {
        // 播放后粒子已脱离玩家层级，销毁玩家时也要回收。
        if (attack1 != null && attack1.transform.parent == null) Destroy(attack1.gameObject);
        if (attack2 != null && attack2.transform.parent == null) Destroy(attack2.gameObject);
        if (attack3 != null && attack3.transform.parent == null) Destroy(attack3.gameObject);
    }

    public void UpdateAttack(int cnt = 1)
    {
        ParticleSystem effect = cnt == 1 ? attack1 : cnt == 2 ? attack2 : cnt == 3 ? attack3 : null;
        if (effect == null) return;
        Transform origin = _origin != null ? _origin : transform;
        effect.transform.SetPositionAndRotation(origin.position, origin.rotation * Quaternion.Euler(0f, 180f, 0f));
        effect.transform.SetParent(null, true);
        effect.Play();
    }
}
