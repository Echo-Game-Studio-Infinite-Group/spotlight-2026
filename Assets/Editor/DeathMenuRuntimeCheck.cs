using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Cinemachine;

[InitializeOnLoad]
internal static class DeathMenuRuntimeCheck
{
    private static double _next;
    private static Vector3 _position;
    private static Quaternion _rotation;
    private static int Stage { get => SessionState.GetInt("DeathCheck", 0); set => SessionState.SetInt("DeathCheck", value); }
    static DeathMenuRuntimeCheck() { EditorApplication.update += Tick; }
    private static void Tick()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (Stage == 0)
        {
            if (!File.Exists("Temp/DeathCheck.request")) return;
            File.Delete("Temp/DeathCheck.request");
            Stage = 1;
            EditorApplication.isPlaying = true;
            _next = EditorApplication.timeSinceStartup + 15;
            return;
        }
        if (!EditorApplication.isPlaying || EditorApplication.timeSinceStartup < _next) return;
        try
        {
            var motor = PlayerMotor.Active;
            var menu = DeathRespawnUI.Instance;
            if (Stage == 1)
            {
                Require(motor != null && menu != null, "自动装配死亡菜单");
                motor.GetComponent<HealthComponent>().TakeEnvironmentalDamage(100000);
                _position = motor.transform.position;
                _rotation = Camera.main.transform.rotation;
                Stage = 2; _next = EditorApplication.timeSinceStartup + 2;
            }
            else if (Stage == 2)
            {
                Require(menu.IsOpen && TimeManager.IsPaused && Time.timeScale == 0, "死亡暂停");
                Require(!motor.GetComponent<PlayerInputReader>().GameplayEnabled && !GameManager.Instance.InputAllowed, "玩家输入锁定");
                Require(Vector3.Distance(_position, motor.transform.position) < 0.001f, "位置冻结");
                Require(Quaternion.Angle(_rotation, Camera.main.transform.rotation) < 0.001f, "视角冻结");
                foreach (var brain in UnityEngine.Object.FindObjectsOfType<CinemachineBrain>()) Require(!brain.enabled, "相机驱动冻结");
                menu.respawnButton.onClick.Invoke();
                Stage = 3; _next = EditorApplication.timeSinceStartup + 8;
            }
            else
            {
                Require(!menu.IsOpen && !TimeManager.IsPaused && motor.GetComponent<HealthComponent>().IsAlive, "重新战斗恢复");
                File.AppendAllText("Temp/DeathCheck.result", "PASS\n");
                Stage = 0; EditorApplication.isPlaying = false;
            }
        }
        catch (Exception e)
        {
            File.AppendAllText("Temp/DeathCheck.result", "FAIL " + e + "\n");
            Stage = 0; EditorApplication.isPlaying = false;
        }
    }
    private static void Require(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        File.AppendAllText("Temp/DeathCheck.result", label + " OK\n");
    }
}
