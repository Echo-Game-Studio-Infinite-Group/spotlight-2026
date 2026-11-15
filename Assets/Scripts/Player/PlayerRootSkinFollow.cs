using UnityEngine;

// 模型中绑在整体根骨上的顶点，倒地时需要跟随身体，结束后还原原始蒙皮引用。
public sealed class PlayerRootSkinFollow
{
    private readonly Transform _hips;
    private readonly Transform _actor;
    private readonly Transform _bodyRoot;
    private readonly SkinnedMeshRenderer[] _skins;
    private readonly Transform[][] _originalBones;
    private Matrix4x4 _hipsToRoot;
    private Transform _follower;

    public bool IsActive => _follower != null;

    public PlayerRootSkinFollow(Animator animator)
    {
        _actor = animator != null ? animator.transform : null;
        _hips = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
        _bodyRoot = _hips != null ? _hips.parent : null;
        _skins = animator != null ? animator.GetComponentsInChildren<SkinnedMeshRenderer>(true) : new SkinnedMeshRenderer[0];
        _originalBones = new Transform[_skins.Length][];
        for (int i = 0; i < _skins.Length; i++) _originalBones[i] = _skins[i].bones;
    }

    public void Begin()
    {
        if (IsActive || _hips == null || _bodyRoot == null || _bodyRoot == _actor) return;
        bool needed = false;
        foreach (Transform[] bones in _originalBones)
            foreach (Transform bone in bones) needed |= bone == _bodyRoot;
        if (!needed) return;

        _hipsToRoot = _hips.worldToLocalMatrix * _bodyRoot.localToWorldMatrix;
        _follower = new GameObject("PlayerRootSkinFollow").transform;
        _follower.gameObject.hideFlags = HideFlags.HideAndDontSave;
        _follower.SetParent(_bodyRoot.parent, false);
        _follower.localScale = _bodyRoot.localScale;
        UpdatePose();
        for (int i = 0; i < _skins.Length; i++)
        {
            Transform[] bones = (Transform[])_originalBones[i].Clone();
            for (int j = 0; j < bones.Length; j++)
                if (bones[j] == _bodyRoot) bones[j] = _follower;
            _skins[i].bones = bones;
        }
    }

    public void UpdatePose()
    {
        if (!IsActive) return;
        Matrix4x4 pose = _hips.localToWorldMatrix * _hipsToRoot;
        _follower.SetPositionAndRotation(pose.GetColumn(3), pose.rotation);
    }

    public void Restore()
    {
        if (!IsActive) return;
        for (int i = 0; i < _skins.Length; i++)
            if (_skins[i] != null) _skins[i].bones = _originalBones[i];
        if (Application.isPlaying) Object.Destroy(_follower.gameObject);
        else Object.DestroyImmediate(_follower.gameObject);
        _follower = null;
    }
}
