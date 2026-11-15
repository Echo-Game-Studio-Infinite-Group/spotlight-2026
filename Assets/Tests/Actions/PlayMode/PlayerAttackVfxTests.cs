#if UNITY_EDITOR
using System.Linq;
using GameJam.Actions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class PlayerAttackVfxTests
{
    private GameObject _root;

    [TearDown]
    public void TearDown()
    {
        if (_root == null) return;
        _root.GetComponent<PlayerActionRunner>().Disconnect();
        Object.DestroyImmediate(_root);
    }

    [TestCase("PlayerAttack", 1)]
    [TestCase("PlayerHighspeed_Attack", 2)]
    [TestCase("PlayerHighspeed_Attack_B", 3)]
    public void EachAttackAlwaysReplaysItsAuthoredSlashAndClearsTheOthers(string name, int expected)
    {
        var action = AssetDatabase.LoadAssetAtPath<ActionDefinition>("Assets/Settings/ActionSequences/Player/" + name + ".asset");
        var frameEvent = action.Timeline.SelectMany(segment => segment.Events).Single(e => e.EventKey == "vfx.attack");
        Assert.AreEqual(expected, frameEvent.Value);
        _root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab"));
        var manager = _root.GetComponent<PlayerVFXManager>();
        Transform host = _root.transform.Find("Vfx/AttackSlashes");
        var groups = host.Cast<Transform>().Select(t => t.GetComponent<ParticleSystem>()).Where(p => p != null).ToArray();
        Assert.AreEqual(4, groups.Length);
        for (int repeat = 0; repeat < 6; repeat++)
        {
            manager.PlayAttack(repeat == 0 ? expected % 3 + 1 : expected);
            if (repeat == 0) continue;
            Assert.That(host.localScale, Is.EqualTo(Vector3.one * 1.2f));
            for (int i = 0; i < groups.Length; i++)
            {
                Assert.AreEqual(i == expected - 1, groups[i].gameObject.activeInHierarchy, "同一动作不能随机换刀光或同时播多组");
                if (i == expected - 1) Assert.IsTrue(groups[i].isPlaying);
                Assert.IsFalse(groups[i].main.loop);
            }
        }
        manager.StopAttacks();
        Assert.IsFalse(host.gameObject.activeSelf);
        Assert.IsTrue(groups.All(group => !group.isPlaying));
    }
}
#endif
