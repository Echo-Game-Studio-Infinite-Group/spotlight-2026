using System.IO;
using UnityEditor;
using UnityEngine;

// 战斗火花特效定妆照工位：批处理出 PNG，供对话里贴图与视觉审计用。
// -executeMethod CombatVfxSnapshot.Run
//
// 原理：编辑模式下粒子不模拟，但 ParticleSystem.Simulate 可以手动推进；
// 相机渲到 RenderTexture 再 ReadPixels 存 PNG，全程无需 Play 模式。
//
// 两套摄影棚（同一批特效各拍一组，文件名前缀区分）：
//   · 暗棚：深色背景，看发光主体本身；
//   · 亮棚：浅灰地板 + 浅灰墙，模拟本作冷白超净室的亮面环境——
//     特效的混合口径（预乘 alpha）必须在亮面上过一遍，暗棚好看不代表亮棚能看。
// 摄影棚只管看清特效，实机还有后处理/场景光，最终观感以 Play 实机为准。
public static class CombatVfxSnapshot
{
    private const string OutDir = "F:/VSCode/taptap-game-jem/_render_vfx";
    private enum StudioMode { Dark, Bright }

    private static GameObject _studioRoot;
    private static Camera _cam;
    private static Light _sun;

    public static void Run()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[CombatVfxSnapshot] 请先退出 Play 模式再执行");
            return;
        }
        Directory.CreateDirectory(OutDir);

        try
        {
            SetupStudio(StudioMode.Dark);
            RenderShots(prefix: "");
            TeardownStudio();

            SetupStudio(StudioMode.Bright);
            RenderShots(prefix: "b_");
            TeardownStudio();
        }
        finally
        {
            TeardownStudio();
        }
        Debug.Log($"[CombatVfxSnapshot] 装配完成：定妆照已写入 {OutDir}（暗棚 + 亮棚两套）");
    }

    // ------------------------------------------------------------------
    // 序列出图（GIF 用）：一次播放 → 逐帧推进 → 逐帧存 PNG
    // -executeMethod CombatVfxSnapshot.Sequence
    //
    // 为什么定妆照不够：静帧只能回答"某一个瞬间好不好看"，而特效是**随时间展开**的——
    // 弧线怎么扫开、火花怎么散、白热闪光多久衰、拖尾留多长，只有连起来才看得出来。
    // （交付/评审里 GIF 也最好传：丢群里就能看，不用开 Unity。）
    public static void Sequence()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[CombatVfxSnapshot] 请先退出 Play 模式再执行");
            return;
        }
        Directory.CreateDirectory(OutDir);

        const int frames = 24;
        const float step = 0.025f;   // 24 × 0.025 = 0.6s：覆盖到火花基本燃尽

        try
        {
            foreach (StudioMode mode in new[] { StudioMode.Dark, StudioMode.Bright })
            {
                SetupStudio(mode);
                string p = mode == StudioMode.Bright ? "b_" : "";

                SeqShot(p + "01_slide",
                    () => { SlideBurst(new Vector3(0.3f, 0.12f, 0f), Vector3.right); return 0f; },
                    new Vector3(-1.2f, 1.0f, -2.7f), new Vector3(0.15f, 0.3f, 0f), frames, step);

                SeqShot(p + "02_hit",
                    () => { CombatSparks.PlayHit(new Vector3(0f, 0.9f, 0f), new Vector3(-0.75f, 0.3f, 0.5f), 25f); return 0f; },
                    new Vector3(1.0f, 1.25f, -2.9f), new Vector3(0f, 0.85f, 0f), frames, step);

                SeqShot(p + "03_parry",
                    () => { CombatSparks.PlayParry(new Vector3(0f, 0.9f, 0f)); return 0f; },
                    new Vector3(0f, 1.15f, -3.1f), new Vector3(0f, 0.85f, 0f), frames, step);

                SeqShot(p + "04_parry_projectile",
                    () => { CombatSparks.PlayParryProjectile(new Vector3(0f, 0.9f, 0f), new Vector3(0f, 0f, 1f)); return 0f; },
                    new Vector3(1.1f, 1.15f, -2.9f), new Vector3(0f, 0.85f, 0f), frames, step);

                TeardownStudio();
            }
        }
        finally
        {
            TeardownStudio();
        }
        Debug.Log($"[CombatVfxSnapshot] 序列出图完成：4 个特效 × 2 棚 × {frames} 帧 → {OutDir}/seq");
    }

    private static void SeqShot(string name, System.Func<float> play, Vector3 camPos, Vector3 lookAt, int frames, float step)
    {
        ClearAllParticles();
        play();                     // 起手不推进：第 0 帧就是"刚触发"的那一刻
        _cam.transform.position = camPos;
        _cam.transform.rotation = Quaternion.LookRotation(lookAt - camPos, Vector3.up);

        string dir = Path.Combine(OutDir, "seq");
        Directory.CreateDirectory(dir);
        for (int i = 0; i < frames; i++)
        {
            if (i > 0) AdvanceAll(step);
            RenderTo(Path.Combine(dir, $"{name}_{i:D2}.png"));
        }
        DestroyTempHosts();         // 宿主留到最后再收：中途销毁等于销毁粒子
        Debug.Log($"[CombatVfxSnapshot] 序列 {name} 完成（{frames} 帧 × {step}s）");
    }

    // 五张：全员合影 + 四张单人近景；parry 多补一张晚帧（闪光散后看火花尾段）
    private static void RenderShots(string prefix)
    {
        Shot(prefix, "05_all", () =>
        {
            CombatSparks.PlayParry(new Vector3(-2.1f, 0.9f, 0f));
            CombatSparks.PlayParryProjectile(new Vector3(-0.7f, 0.9f, 0f), new Vector3(0.4f, -0.2f, 1f));
            CombatSparks.PlayHit(new Vector3(0.7f, 0.9f, 0f), new Vector3(-0.75f, 0.3f, 0.5f), 25f);
            SlideBurst(new Vector3(2.1f, 0.12f, 0f), Vector3.right);
            return 0.09f;
        }, new Vector3(0f, 1.35f, -4.8f), new Vector3(0f, 0.8f, 0f));

        Shot(prefix, "03_parry", () =>
        {
            CombatSparks.PlayParry(new Vector3(0f, 0.9f, 0f));
            return 0.05f; // 白热闪光只有 0.06s，趁亮抓
        }, new Vector3(0f, 1.15f, -3.1f), new Vector3(0f, 0.85f, 0f));

        Shot(prefix, "06_parry_late", () =>
        {
            CombatSparks.PlayParry(new Vector3(0f, 0.9f, 0f));
            return 0.17f; // 晚帧：环扩张到后半程、火花散开
        }, new Vector3(0f, 1.15f, -3.1f), new Vector3(0f, 0.85f, 0f));

        Shot(prefix, "04_parry_projectile", () =>
        {
            // 弹道从画面深处朝镜头飞来，parry 弹开的火花朝镜头方向反喷
            CombatSparks.PlayParryProjectile(new Vector3(0f, 0.9f, 0f), new Vector3(0f, 0f, 1f));
            return 0.05f;
        }, new Vector3(1.1f, 1.15f, -2.9f), new Vector3(0f, 0.85f, 0f));

        Shot(prefix, "02_hit", () =>
        {
            CombatSparks.PlayHit(new Vector3(0f, 0.9f, 0f), new Vector3(-0.75f, 0.3f, 0.5f), 25f);
            return 0.11f;
        }, new Vector3(1.0f, 1.25f, -2.9f), new Vector3(0f, 0.85f, 0f));

        Shot(prefix, "01_slide", () =>
        {
            SlideBurst(new Vector3(0.3f, 0.12f, 0f), Vector3.right);
            return 0.09f;
        }, new Vector3(-1.2f, 1.0f, -2.7f), new Vector3(0.15f, 0.3f, 0f));

        // 诊断：滑铲近景历史上出过空图，补拍两个机位 + 渲染器状态，定位断在哪一层
        ParticleSystem slidePs = Object.FindObjectOfType<FrictionSparks>()?.GetComponentInChildren<ParticleSystem>();
        if (slidePs != null)
        {
            ParticleSystemRenderer r = (ParticleSystemRenderer)slidePs.GetComponent<Renderer>();
            Debug.Log($"[CombatVfxSnapshot] slide诊断: renderer.enabled={r.enabled} "
                + $"bounds={r.bounds.center}~{r.bounds.extents} mat={(r.material != null ? r.material.shader.name : "null")} "
                + $"goActive={slidePs.gameObject.activeInHierarchy} count={slidePs.particleCount}");
            _cam.transform.position = new Vector3(0f, 1.35f, -4.8f);
            _cam.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 0.8f, 0f) - _cam.transform.position, Vector3.up);
            RenderTo(Path.Combine(OutDir, prefix + "diag_slide_wide.png"));
            _cam.transform.position = new Vector3(0.3f, 0.45f, -1.6f);
            _cam.transform.rotation = Quaternion.LookRotation(new Vector3(0.3f, 0.12f, 0f) - _cam.transform.position, Vector3.up);
            RenderTo(Path.Combine(OutDir, prefix + "diag_slide_closeup2.png"));
        }
    }

    // 摆一个滑铲火花：直接复用 FrictionSparks 的发射器装配，手动 Emit + 转向，
    // 贴地朝行进反方向喷（真实游戏里由移动状态按每米路程驱动，这里摆拍等价取样）。
    private static void SlideBurst(Vector3 position, Vector3 travelDirection)
    {
        GameObject player = new GameObject("SnapshotSlidePlayer");
        player.transform.position = position;
        player.AddComponent<FrictionSparks>();
        player.GetComponent<FrictionSparks>().EnsureEmitters();
        ParticleSystem emitter = player.GetComponentInChildren<ParticleSystem>();
        emitter.transform.rotation = Quaternion.LookRotation(-travelDirection, Vector3.up);
        emitter.Emit(90);
        Debug.Log($"[CombatVfxSnapshot] slide Emit后 particleCount={emitter.particleCount} isPlaying={emitter.isPlaying}");
        // 宿主先留着：粒子跟着粒子系统走，销毁发射器=销毁粒子（世界坐标系也救不了它）。
        // 出图后由 Shot 统一收走。
        _tempHosts.Add(player);
    }

    private static readonly System.Collections.Generic.List<GameObject> _tempHosts = new System.Collections.Generic.List<GameObject>();

    private static void DestroyTempHosts()
    {
        foreach (GameObject host in _tempHosts)
        {
            if (host != null) Object.DestroyImmediate(host);
        }
        _tempHosts.Clear();
    }

    private static void Shot(string prefix, string name, System.Func<float> play, Vector3 camPos, Vector3 lookAt)
    {
        ClearAllParticles(); // 批处理没有帧循环，上一张的粒子不会自然燃尽，必须手动清
        float t = play();
        AdvanceAll(t);
        _cam.transform.position = camPos;
        _cam.transform.rotation = Quaternion.LookRotation(lookAt - camPos, Vector3.up);
        RenderTo(Path.Combine(OutDir, prefix + name + ".png"));
        DestroyTempHosts();
        Debug.Log($"[CombatVfxSnapshot] {prefix}{name}.png 完成（播放后 {t:0.###}s 抓帧）");
    }

    private static void ClearAllParticles()
    {
        foreach (ParticleSystem ps in Object.FindObjectsOfType<ParticleSystem>())
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    // 所有粒子统一推进一小步（withChildren=false：逐系统推进，避免父子重复积分）
    private static void AdvanceAll(float dt)
    {
        if (dt <= 0f) return;
        foreach (ParticleSystem ps in Object.FindObjectsOfType<ParticleSystem>())
            ps.Simulate(dt, false, false);
    }

    private static void SetupStudio(StudioMode mode)
    {
        _studioRoot = new GameObject($"SnapshotStudio({mode})");

        GameObject camGo = new GameObject("SnapshotCamera");
        camGo.transform.SetParent(_studioRoot.transform, false);
        _cam = camGo.AddComponent<Camera>();
        _cam.backgroundColor = mode == StudioMode.Bright ? new Color(0.53f, 0.58f, 0.65f) : new Color(0.07f, 0.08f, 0.11f);
        _cam.clearFlags = CameraClearFlags.SolidColor;
        _cam.fieldOfView = 45f;
        _cam.nearClipPlane = 0.05f;

        GameObject sunGo = new GameObject("SnapshotSun");
        sunGo.transform.SetParent(_studioRoot.transform, false);
        _sun = sunGo.AddComponent<Light>();
        _sun.type = LightType.Directional;
        _sun.intensity = mode == StudioMode.Bright ? 1.15f : 1.1f;
        _sun.shadows = LightShadows.None;
        sunGo.transform.rotation = Quaternion.Euler(45f, -25f, 0f);

        // 地板：暗棚深灰、亮棚浅灰（对着实机白超净室的地面明度）
        Material groundMat = NewUnlitMaterial(mode == StudioMode.Bright
            ? new Color(0.78f, 0.8f, 0.84f)
            : new Color(0.16f, 0.17f, 0.21f));
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "SnapshotGround";
        ground.transform.SetParent(_studioRoot.transform, false);
        ground.transform.position = Vector3.zero;
        Object.DestroyImmediate(ground.GetComponent<Collider>());
        ground.GetComponent<Renderer>().sharedMaterial = groundMat;

        if (mode == StudioMode.Bright)
        {
            // 亮棚加一面浅灰墙当背景：火花与「墙面」的对比关系和走廊场景接近
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "SnapshotWall";
            wall.transform.SetParent(_studioRoot.transform, false);
            wall.transform.position = new Vector3(0f, 2.2f, 3f);
            wall.transform.localScale = new Vector3(12f, 4.5f, 0.1f);
            Object.DestroyImmediate(wall.GetComponent<Collider>());
            wall.GetComponent<Renderer>().sharedMaterial = NewUnlitMaterial(new Color(0.62f, 0.66f, 0.72f));
        }
    }

    private static Material NewUnlitMaterial(Color color)
    {
        Material material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        material.SetColor("_BaseColor", color);
        return material;
    }

    private static void TeardownStudio()
    {
        DestroyTempHosts();
        if (_studioRoot != null) Object.DestroyImmediate(_studioRoot);
        _studioRoot = null;
        _cam = null;
        _sun = null;
    }

    private static void RenderTo(string path)
    {
        const int width = 1280, height = 720;
        RenderTexture rt = new RenderTexture(width, height, 24);
        try
        {
            _cam.targetTexture = rt;
            _cam.Render();
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            _cam.targetTexture = null;
            RenderTexture.active = null;
        }
        finally
        {
            rt.Release();
            Object.DestroyImmediate(rt);
        }
    }
}
