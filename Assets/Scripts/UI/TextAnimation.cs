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

        aoc["PLACEHOLDERAPPEARANCE"] = _appearanceAnimation;
        aoc["PLACEHOLDERDISAPPEARANCE"] = _disappearanceAnimation;

        GetComponent<MeshRenderer>().enabled = false;

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

        yield return new WaitForSeconds(_disappearanceAnimation.length + 0.1f);

        GetComponent<MeshRenderer>().enabled = false;
        gameObject.SetActive(false);
    }
}
