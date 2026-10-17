using System.Collections;
using UnityEngine;

public class TextAppearance : MonoBehaviour
{
    [SerializeField] private float _dist = 0.5f;
    [SerializeField] private float _padding = 0.1f;
    [SerializeField] private AnimationClip _appearanceAnimation;
    [SerializeField] private float _duration;
    [SerializeField] private AnimationClip _disappearanceAnimation;

    private Animator _animator;

    private void Start()
    {
        _animator = GetComponent<Animator>();
        AnimatorOverrideController aoc = new AnimatorOverrideController(_animator.runtimeAnimatorController);
        _animator.runtimeAnimatorController = aoc;

        aoc["Appearance"] = _appearanceAnimation;
        aoc["Disappearance"] = _disappearanceAnimation;

        StartCoroutine(Check());
    }

    private IEnumerator Check()
    {
        // Camera.main 在场景刚加载的几帧可能还是 null
        while (Camera.main == null || !VisibilityChecker.isVisible(Camera.main, transform, _dist, _padding))
        {
            yield return new WaitForSeconds(0.2f);
        }

        GetComponent<MeshRenderer>().enabled = true;
        _animator.Play("Appearance");

        yield return new WaitForSeconds(_appearanceAnimation.length + _duration);

        _animator.Play("Disappearance");
    }

    /// <summary>逐帧把剪辑采样到本对象上，只改它自己的材质实例。</summary>
    private IEnumerator Sample(AnimationClip clip)
    {
        MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
        Material own = meshRenderer != null ? meshRenderer.sharedMaterial : null;

        float time = 0f;
        while (time < clip.length)
        {
            if (own != null)
            {
                // 剪辑里那条换材质曲线可能把共享资产塞了进来，先装回自己的实例
                meshRenderer.sharedMaterial = own;
            }

            clip.SampleAnimation(gameObject, time);

            if (own != null && meshRenderer.sharedMaterial != own)
            {
                // 采样期间材质被换成共享资产：把这一帧的值搬回实例，再装回去
                own.CopyPropertiesFromMaterial(meshRenderer.sharedMaterial);
                meshRenderer.sharedMaterial = own;
            }

            time += Time.deltaTime;
            yield return null;
        }

        clip.SampleAnimation(gameObject, clip.length);
    }
}
