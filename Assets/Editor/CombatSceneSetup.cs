using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// 战斗装配：把「玩家攻击 + 剑 + 敌人 + 震屏」这一整套接线落到资产与场景里。
// 为什么用编辑器工具而不是手改 .prefab/.unity：
//   · 动画状态机、骨骼查找、组件引用都依赖 Unity 的类型化 API，手写 YAML 的 fileID 极易写错；
//   · 与 ProjectBootstrap 同一套路，可重复执行（幂等），出错时能从日志直接定位。
// 执行入口：菜单「超高速行者/装配战斗与敌人」，或命令行 -executeMethod CombatSceneSetup.Build
public static class CombatSceneSetup
{
    private const string ScenePath = "Assets/Scenes/TestScene.unity";
    private const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";
    private const string SwordPrefabPath = "Assets/Prefabs/Sword.prefab";
    private const string PopupPrefabPath = "Assets/Prefabs/DamagePopup.prefab";
    private const string AttackClipPath = "Assets/Animations/fbx/Attack.fbx";
    private const string EnemyName = "MikuGandam";
    private const string CameraName = "Main Camera";

    // 剑在右手骨骼上的摆放：不同骨架差异很大，这两个值由实测调整后固化，改模型时需要重新对一次
    private static readonly Vector3 SwordLocalPosition = new Vector3(0f, 0.12f, 0f);
    private static readonly Vector3 SwordLocalEulerAngles = new Vector3(0f, 0f, 0f);
    private static readonly Vector3 SwordLocalScale = Vector3.one;

    // 玩家实例在 TestScene 里位于 (-13.87, 0, -23.18)，敌人放在它正前方 5 米，
    // 保证一按播放就能看到并打到；世界原点那边是空场地，放过去会看起来「敌人没生成」。
    private static readonly Vector3 EnemySpawnPosition = new Vector3(-13.8672f, 1f, -18.1813f);
    private const float EnemyMaxHealth = 120f;
    private const float EnemyAttackPower = 8f;
    private const float EnemyMoveSpeed = 4f;
    private const float PlayerAttackDamage = 25f;
    private const float PlayerMaxHealth = 100f;
    // 攻击窗口的兜底值：正常情况由 Attack.fbx 的片段时长覆盖（见 SetupPlayerPrefab）
    private const float PlayerAttackDuration = 1.6f;
    // 跳字存活与上浮速度：0.7s 在高速战斗里来不及被看到
    private const float PopupLifetime = 1.2f;
    private const float PopupRiseSpeed = 2.2f;
    private const float EnemyShakeAmplitude = 0.7f;
    // 敌人的无敌帧：照参考实现 Player.cs 的 invulnerableTime 取 0.45s。
    // 判定已回到「只靠 OnTriggerEnter」，一次挥砍最多结算一次，
    // 这个值只用于挡住极短时间内的重复触发，不再承担节流职责。
    private const float EnemyInvulnerableTime = 0.45f;
    // 敌人是否追击玩家：默认关闭 —— 追击需要 CharacterController，
    // 而它的胶囊以包围盒为基准体积可观，会改变敌人外观。需要时在编辑器里改这里即可。
    private const bool EnemyChasePlayer = false;
    private const float HitTimeScale = 0.75f;
    private const float HitSlowSeconds = 0.2f;

    [MenuItem("超高速行者/装配战斗与敌人")]
    public static void Build()
    {
        // 播放模式下不能打开/保存场景，先退出再重试（与 ProjectBootstrap 同一处坑）
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[CombatSceneSetup] 检测到 Play 模式：战斗装配只在编辑模式生效，正在退出播放模式后重试…");
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.isPlaying = false;
            return;
        }

        RunBuild();
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode) return;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.delayCall += RunBuild;
    }

    private static void RunBuild()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[CombatSceneSetup] 仍处于 Play 模式，装配中止。");
            return;
        }

        ResetSwordPrefabTransform();
        // 先写动画事件：Hitbox 的开关完全由它驱动，必须在装配前就位
        ConfigureAttackAnimationEvents();
        EnsureDamagePopupPrefab();
        PlayerAnimationSetup.EnsureStateLayout();
        SetupPlayerPrefab();
        SetupScene();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Verify();
        Debug.Log("[CombatSceneSetup] 战斗装配完成");
    }

    // Sword.prefab 上残留着「当年拖进场景时」的世界坐标（-13.4, 1.4, -22.8）与 0.1 的缩放。
    // 它作为手的子物体被实例化时会继承这份垃圾位移，剑会飞到手外面几十米，必须先把预制体本身清干净。
    private static void ResetSwordPrefabTransform()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(SwordPrefabPath);
        if (root == null)
        {
            Debug.LogError("[CombatSceneSetup] 找不到 " + SwordPrefabPath);
            return;
        }

        try
        {
            bool dirty = false;
            if (root.transform.localPosition != Vector3.zero)
            {
                root.transform.localPosition = Vector3.zero;
                dirty = true;
            }

            if (root.transform.localRotation != Quaternion.identity)
            {
                root.transform.localRotation = Quaternion.identity;
                dirty = true;
            }

            if (root.transform.localScale != Vector3.one)
            {
                root.transform.localScale = Vector3.one;
                dirty = true;
            }

            if (dirty) PrefabUtility.SaveAsPrefabAsset(root, SwordPrefabPath);
            Debug.Log("[CombatSceneSetup] Sword.prefab 根节点变换已归零: " + dirty);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static void SetupPlayerPrefab()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
        if (root == null)
        {
            Debug.LogError("[CombatSceneSetup] 找不到 " + PlayerPrefabPath);
            return;
        }

        try
        {
            // 先清失效组件：只要还剩一个 m_Script = 0 的组件，Unity 就会拒绝保存整个预制体，
            // 后面所有装配改动都会静默丢失。
            StripMissingScripts(root, "Player.prefab");

            // 玩家身份组件：ProjectBootstrap 时代没有把它装到预制体上，导致敌人 FindObjectOfType<Player>()
            // 找不到目标、站着不动，GameManager.RegisterPlayer 也从未被调用。战斗系统依赖它定位玩家。
            Player identity = root.GetComponent<Player>();
            if (identity == null) identity = root.AddComponent<Player>();

            PlayerCombat combat = root.GetComponent<PlayerCombat>();
            if (combat == null) combat = root.AddComponent<PlayerCombat>();
            combat.Configure(PlayerAttackDamage, PlayerAttackDuration, HitSlowSeconds, HitTimeScale);
            // 攻击窗口按「动作实际时长」定，不是片段全长：
            // 片段 3.782s 里只有前 1.4s 在动，按全长设窗口会白锁玩家 2.4 秒。
            float clipLength = MeasureClipLength(AttackClipPath);
            float motionEnd = MeasureMotionEnd(LoadClipAfterImport(AttackClipPath));
            if (motionEnd > 0f)
            {
                combat.SetAttackDuration(motionEnd * AttackWindowTailRatio);
                Debug.Log($"[CombatSceneSetup] 攻击窗口 = 动作时长 {motionEnd:0.###}s × {AttackWindowTailRatio:0.##} "
                    + $"= {motionEnd * AttackWindowTailRatio:0.###}s（片段全长 {clipLength:0.###}s，"
                    + $"砍掉了 {clipLength - motionEnd:0.###}s 的静止尾巴）");
            }
            else if (clipLength > 0f)
            {
                combat.SetAttackDuration(clipLength);
            }

            // 攻击判定盒 + 特效管理器。
            // 判定盒不再写进预制体：它必须挂在手骨下，而手骨来自 fbx，写进预制体的父级关系
            // 在换模型/重导入时容易失效（本项目遇到过渡PrefabInstance 块存在却不再实例化）。
            // 改由 SwordBinder 在运行时把手骨、剑、判定盒一起建出来，引用不会因序列化而丢失。
            SwordBinder binderForHitbox = root.GetComponent<SwordBinder>();
            if (binderForHitbox != null)
            {
                binderForHitbox.SetHitboxParameters(PlayerHitboxRadius, PlayerHitboxOffset);
                EditorUtility.SetDirty(binderForHitbox);
            }

            // 预制体里若残留旧的判定盒节点，清掉避免出现两个
            Transform staleHitbox = root.transform.Find("PlayerHitbox");
            if (staleHitbox != null)
            {
                Object.DestroyImmediate(staleHitbox.gameObject, true);
                Debug.Log("[CombatSceneSetup] 已移除预制体里残留的 PlayerHitbox（改由运行时创建）");
            }

            AttackVFXManager vfx = EnsureAttackVfx(root, combat);
            combat.SetReferences(null, vfx);

            // 玩家受击标记：敌人的 Hitbox 靠它识别玩家（玩家走 PlayerHealth，不实现 IDamageable）
            if (root.GetComponent<PlayerHitboxTarget>() == null) root.AddComponent<PlayerHitboxTarget>();

            // 玩家生命值：敌人扣血走它，而不是 GameManager.Player（后者的 Instance getter
            // 自动 new 出来的空壳没有 Awake，字段全为 null）
            PlayerHealth health = root.GetComponent<PlayerHealth>();
            if (health == null) health = root.AddComponent<PlayerHealth>();
            health.Configure(PlayerMaxHealth);

            SwordBinder binder = root.GetComponent<SwordBinder>();
            if (binder == null) binder = root.AddComponent<SwordBinder>();
            Transform sword = EnsureSwordInstance(root);
            binder.Configure(sword, "mixamorig:RightHand", SwordLocalPosition, SwordLocalEulerAngles, SwordLocalScale);

            // 动画事件接收器必须挂在 Animator 所在节点（PlayerDummy）上：
            // Unity 的动画事件按「动画器所在 GameObject」派发，挂在根节点会报
            // "AnimationEvent has no receiver!"，判定与特效在真实游玩时全部失效。
            Transform animated = root.transform.Find("PlayerDummy");
            if (animated != null)
            {
                AttackAnimationEventReceiver receiver = animated.GetComponent<AttackAnimationEventReceiver>();
                if (receiver == null) receiver = animated.gameObject.AddComponent<AttackAnimationEventReceiver>();
                receiver.Configure(combat, null);
                EditorUtility.SetDirty(receiver);
            }
            else
            {
                Debug.LogError("[CombatSceneSetup] 找不到 PlayerDummy 节点，动画事件无处投递");
            }

            EditorUtility.SetDirty(identity);
            EditorUtility.SetDirty(combat);
            EditorUtility.SetDirty(binder);
            EditorUtility.SetDirty(vfx);
            PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
            Debug.Log($"[CombatSceneSetup] Player.prefab 已装配: PlayerCombat + SwordBinder（运行时建剑与判定盒 "
                + $"半径 {PlayerHitboxRadius}）+ 攻击特效 + PlayerHitboxTarget");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // 剑作为嵌套预制体实例挂在玩家根下，运行时再由 SwordBinder 移到手骨上：
    // 直接把手骨当父级写进预制体，换骨架/换模型时引用会静默丢失，且预制体无法保存对 fbx 内部骨骼的父级关系。
    private static Transform EnsureSwordInstance(GameObject playerRoot)
    {
        Transform existing = FindChildByName(playerRoot.transform, "Sword");
        if (existing != null && existing.parent == playerRoot.transform) return existing;

        GameObject swordPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SwordPrefabPath);
        if (swordPrefab == null)
        {
            Debug.LogError("[CombatSceneSetup] 找不到 " + SwordPrefabPath);
            return null;
        }

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(swordPrefab, playerRoot.transform);
        instance.name = "Sword";
        instance.transform.localPosition = SwordLocalPosition;
        instance.transform.localRotation = Quaternion.Euler(SwordLocalEulerAngles);
        instance.transform.localScale = SwordLocalScale;
        return instance.transform;
    }

    private static void SetupScene()
    {
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        EnsureCombatClock();
        EnsureCameraShaker(scene);
        SetupEnemy(scene);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    // TimeManager 原本只在 BootScene 里存在，直接播 TestScene 时它不会创建，
    // 攻击窗口用的是 TimeManager.PlayerTime，缺了它整套时间系统都退化成默认值。
    private static void EnsureCombatClock()
    {
        if (Object.FindObjectOfType<TimeManager>() != null) return;
        GameObject host = new GameObject("TimeManager");
        host.AddComponent<TimeManager>();
        Debug.Log("[CombatSceneSetup] 场景缺少 TimeManager，已补建");
    }

    private static void EnsureCameraShaker(Scene scene)
    {
        Camera camera = Camera.main;
        if (camera == null)
        {
            Debug.LogError("[CombatSceneSetup] 场景里找不到带 MainCamera 标签的相机，震屏不会生效");
            return;
        }

        CameraShaker shaker = camera.GetComponent<CameraShaker>();
        if (shaker == null) shaker = camera.gameObject.AddComponent<CameraShaker>();
        // 震屏幅度由受击方决定，这里的默认值只在没有覆盖时兜底
        shaker.Configure(null, 0.6f);
        EditorUtility.SetDirty(shaker);
        Debug.Log("[CombatSceneSetup] 震屏已挂到相机: " + camera.name);
    }

    private static void SetupEnemy(Scene scene)
    {
        GameObject enemy = FindInScene(scene, EnemyName);
        if (enemy == null)
        {
            Debug.LogError($"[CombatSceneSetup] 场景里找不到敌人对象「{EnemyName}」");
            return;
        }

        // 只在首次装配时动位置：每次执行都重置会让主人在编辑器里摆好的位置白改。
        // 判定依据是「组件是否已存在」——已装配过就只补齐缺失项，其余一律尊重现状。
        bool alreadyWired = enemy.GetComponent<Enemy>() != null;

        // 敌人的位移方式：默认沿用场景原来的 Rigidbody（与地图物件一致），
        // 只有显式要求「会追人」时才挂 CharacterController —— 那是为了直线追击，
        // 而它的胶囊以包围盒为基准，尺寸可观，不该在不需要追击时强加给敌人。
        Rigidbody body = enemy.GetComponentInChildren<Rigidbody>();
        if (EnemyChasePlayer)
        {
#pragma warning disable CS0162 // EnemyChasePlayer 是编译期常量，关闭追击时这条分支不可达是预期行为
            ConfigureChaseMovement(enemy, body);
#pragma warning restore CS0162
        }
        else
        {
            RemoveChaseMovement(enemy);
        }

        Enemy enemyComponent = enemy.GetComponent<Enemy>();
        if (enemyComponent == null) enemyComponent = enemy.AddComponent<Enemy>();
        enemyComponent.ConfigureStats(EnemyMaxHealth, EnemyAttackPower, EnemyMoveSpeed);
        enemyComponent.SetChasePlayer(EnemyChasePlayer);
        // 无敌帧必须短：Hitbox 是周期性重叠检测（不是一次性的 enter 事件），
        // 这个值直接决定「同一刀最多结算几次」。设长了会让整刀被吃掉（砍到不掉血）。
        enemyComponent.SetInvulnerableTime(EnemyInvulnerableTime);
        ApplyDamageFeedback(enemyComponent);
        EditorUtility.SetDirty(enemyComponent);

        // 受击盒是必需的：攻击检测要靠碰撞体命中 Damageable。
        // 早先这个角色由 CharacterController 的胶囊兼任，移除追击模式后敌人就变成打不到的空壳。
        EnsureHitBox(enemy);
        // 敌人自己的攻击判定盒与特效：开关同样由动画事件驱动（缺事件时回退到 TryAttack 的代码结算）
        Hitbox enemyHitbox = EnsureHitbox(enemy, "EnemyHitbox", EnemyHitboxCenter, EnemyHitboxSize,
            CampType.Enemy, enemyComponent, onRightHand: false);
        AttackVFXManager enemyVfx = EnsureAttackVfx(enemy, enemyComponent);
        enemyComponent.ConfigureAttackHitbox(enemyHitbox, enemyVfx);
        EditorUtility.SetDirty(enemyHitbox);
        EditorUtility.SetDirty(enemyVfx);
        // 跳字组件实例：命中时复用，不再运行时生成
        AttachDamagePopup(enemy, enemyComponent);

        if (!alreadyWired)
        {
            enemy.transform.position = EnemySpawnPosition;
            Debug.Log($"[CombatSceneSetup] 敌人 {enemy.name} 首次装配，位置设为 {enemy.transform.position}");
        }

        Debug.Log($"[CombatSceneSetup] 敌人已装配: {enemy.name} 位置={enemy.transform.position} "
            + $"缩放={enemy.transform.localScale} 追击={EnemyChasePlayer} "
            + $"CharacterController={enemy.GetComponent<CharacterController>() != null} "
            + $"残余刚体={(body != null ? body.name : "无")}");
    }

    // 直线追击需要 CharacterController；它的胶囊以包围盒为基准，会改变敌人的外观体积，
    // 所以只在明确需要追击时才配置，且必须停掉刚体（两套推进同时存在会互相顶）。
    private static void ConfigureChaseMovement(GameObject enemy, Rigidbody body)
    {
        CharacterController controller = enemy.GetComponent<CharacterController>();
        if (controller == null) controller = enemy.AddComponent<CharacterController>();
        Bounds bounds = CalculateBounds(enemy);
        controller.center = new Vector3(0f, bounds.extents.y, 0f);
        controller.height = Mathf.Max(1f, bounds.size.y);
        controller.radius = Mathf.Clamp(Mathf.Max(bounds.extents.x, bounds.extents.z) * 0.5f, 0.3f, 2f);
        controller.slopeLimit = 55f;
        controller.stepOffset = 0.4f;
        EditorUtility.SetDirty(controller);

        if (body == null) return;
        body.isKinematic = true;
        body.useGravity = false;
        EditorUtility.SetDirty(body);
    }

    // 静态靶子：不需要 CharacterController，留着只会凭空多出一个可观的胶囊
    private static void RemoveChaseMovement(GameObject enemy)
    {
        CharacterController stale = enemy.GetComponent<CharacterController>();
        if (stale == null) return;
        Object.DestroyImmediate(stale, true);
        Debug.Log("[CombatSceneSetup] 敌人不追击，已移除多余的 CharacterController");
    }

    // 受击盒：按渲染包围盒拟合一个 BoxCollider，作为攻击检测能命中的目标。
    // 只加在「根部没有任何碰撞体」的情况下 —— 模型自带碰撞体时不重复叠一层。
    private static void EnsureHitBox(GameObject enemy)
    {
        if (enemy.GetComponent<Collider>() != null)
        {
            Debug.Log($"[CombatSceneSetup] {enemy.name} 根部已有碰撞体，跳过受击盒");
            return;
        }

        Bounds bounds = CalculateBounds(enemy);
        BoxCollider hitBox = enemy.AddComponent<BoxCollider>();
        // 世界包围盒换算到本地空间：敌人根节点缩放为 1，直接偏移即可
        Vector3 worldCenter = bounds.center;
        hitBox.center = enemy.transform.InverseTransformPoint(worldCenter);
        hitBox.size = bounds.size;
        // 留一点余量，贴着模型边缘时不会因为浮点误差漏判
        hitBox.size = new Vector3(hitBox.size.x * 1.05f, hitBox.size.y * 1.02f, hitBox.size.z * 1.05f);
        EditorUtility.SetDirty(hitBox);
        Debug.Log($"[CombatSceneSetup] 已为 {enemy.name} 添加受击盒 中心={hitBox.center} 尺寸={hitBox.size}");
    }

    // 构建伤害跳字预制体：把 Canvas / Text / Outline / CanvasGroup 一次性搭好存成资产。
    // 运行时只做「激活 + 重置」复用，不再每次命中 new GameObject 与 AddComponent。
    private static void EnsureDamagePopupPrefab()
    {
        GameObject root = new GameObject("DamagePopup");
        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 100;

        // 世界空间 Canvas 必须缩放：uGUI 的 1 单位等于 1 像素，不缩会是一块巨大的面板。
        // 0.022 让 72 号字在世界上约 1.6 米高，高速战斗里一眼可见。
        root.transform.localScale = Vector3.one * 0.022f;
        RectTransform rect = root.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(400f, 160f);

        GameObject textObject = new GameObject("Label");
        textObject.transform.SetParent(root.transform, false);
        Text label = textObject.AddComponent<Text>();
        label.fontSize = 72;
        label.fontStyle = FontStyle.Bold;
        label.alignment = TextAnchor.MiddleCenter;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        label.color = new Color(1f, 0.85f, 0.2f);
        label.raycastTarget = false;

        Outline outline = textObject.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.95f);
        outline.effectDistance = new Vector2(3f, -3f);

        RectTransform textRect = textObject.GetComponent<RectTransform>();
        textRect.sizeDelta = rect.sizeDelta;
        textRect.anchoredPosition = Vector2.zero;

        CanvasGroup group = root.AddComponent<CanvasGroup>();
        group.alpha = 1f;

        DamagePopup popup = root.AddComponent<DamagePopup>();
        popup.ConfigureReferences(canvas, label, outline, group);
        popup.ConfigureAnimation(PopupLifetime, PopupRiseSpeed);

        PrefabUtility.SaveAsPrefabAsset(root, PopupPrefabPath);
        Object.DestroyImmediate(root);
        Debug.Log($"[CombatSceneSetup] 已生成跳字预制体: {PopupPrefabPath}");
    }

    // 在敌人身上放一个跳字实例并接给 Damageable。
    // 实例化后保持激活以便连接引用，由 Damageable 在 Awake 里统一隐藏，避免它在场景里露出来。
    private static void AttachDamagePopup(GameObject enemy, Damageable damageable)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PopupPrefabPath);
        if (prefab == null)
        {
            Debug.LogError($"[CombatSceneSetup] 找不到跳字预制体 {PopupPrefabPath}");
            return;
        }

        DamagePopup existing = enemy.GetComponentInChildren<DamagePopup>(true);
        if (existing == null)
        {
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, enemy.transform);
            instance.name = "DamagePopup";
            instance.transform.localPosition = Vector3.zero;
            existing = instance.GetComponent<DamagePopup>();
        }

        damageable.SetDamagePopup(existing);
        EditorUtility.SetDirty(damageable);
        Debug.Log($"[CombatSceneSetup] 跳字组件已挂到 {enemy.name}: {(existing != null)}");
    }

    // ===== 攻击判定与特效：移植参考实现 LittleAdventure 的「动画事件开关 Hitbox」方案 =====
    //
    // 参考实现的三个连招里，Hitbox 事件落在动画的 23%~26% 起、72%~81% 收，
    // 也就是判定窗口只占中间一段，前后各留前摇与后摇。这里按同样的占比给 spotlight 的挥砍估算。
    private const float HitboxStartRatio = 0.24f;
    private const float HitboxEndRatio = 0.81f;
    // 特效比判定略早触发，让斩击面先出来、判定紧随其后
    private const float AttackVfxRatio = 0.20f;
    // 攻击窗口 = 动作时长 × 此系数。留一点余量让挥砍动作收干净，
    // 又不把后面的静止尾巴算进去（片段里的尾巴会让玩家白等 2 秒多）。
    private const float AttackWindowTailRatio = 0.9f;
    // 动画事件布局版本：改动事件名/时间比例后加一，旧事件会被识别为过期并重写。
    // 用 stringParameter 存这个标记，就能在不重导入的前提下判断布局是否最新。
    private const int EventLayoutVersion = 2;
    private static string EventLayoutTag => $"combat-v{EventLayoutVersion}";

    private static readonly Vector3 PlayerHitboxCenter = new Vector3(0f, 0.55f, 0.25f);
    private static readonly Vector3 PlayerHitboxSize = new Vector3(0.45f, 0.45f, 1.2f);
    // 玩家判定球：剑长约 1.6m，半径 0.9 能覆盖挥砍范围；偏移沿手骨本地 Z 向前
    private const float PlayerHitboxRadius = 0.9f;
    private static readonly Vector3 PlayerHitboxOffset = new Vector3(0f, 0f, 0.45f);
    private static readonly Vector3 EnemyHitboxCenter = new Vector3(0f, 1.1f, 1.6f);
    private static readonly Vector3 EnemyHitboxSize = new Vector3(2f, 2.2f, 2.2f);

    // 给 Attack.fbx 写入动画事件：开启判定 / 播放特效 / 关闭判定。
    // 写入后 Unity 会重新导入该 fbx，事件才会真正挂到片段上。
    private static void ConfigureAttackAnimationEvents()
    {
        ModelImporter importer = AssetImporter.GetAtPath(AttackClipPath) as ModelImporter;
        if (importer == null)
        {
            Debug.LogError($"[CombatSceneSetup] 无法读取 {AttackClipPath} 的导入设置");
            return;
        }

        ModelImporterClipAnimation[] clips = importer.clipAnimations;
        if (clips == null || clips.Length == 0) clips = importer.defaultClipAnimations;
        if (clips.Length == 0)
        {
            Debug.LogError("[CombatSceneSetup] Attack.fbx 里没有可配置的动画剪辑");
            return;
        }

        ModelImporterClipAnimation clip = clips[0];

        // 时长基准用「动作实际时长」，与攻击窗口保持一致：
        // 判定与特效都应落在动作区间内，不该跑到后面的静止尾巴里。
        AnimationClip imported = LoadClipAfterImport(AttackClipPath);
        float motionEnd = MeasureMotionEnd(imported);
        float length = imported != null && imported.length > 0.01f ? imported.length : 1.6f;
        if (motionEnd <= 0f) motionEnd = length;

        float vfxTime = motionEnd * AttackVfxRatio;
        float enableTime = motionEnd * HitboxStartRatio;
        float disableTime = motionEnd * HitboxEndRatio;

        // 幂等闸门：布局没变就跳过。
        // 为什么必须有：每次写 clipAnimations 都会触发重导入，而重导入会把片段长度往上带
        // （实测一路增长到 3.782s），事件时间又是按长度比例算的 —— 无闸门就会每跑一次漂移一次。
        // 带一个版本标记：改了布局后把 EventLayoutVersion 加一，旧事件会被识别为过期并重写。
        if (HasHitboxEvents(clip) && IsEventLayoutCurrent(clip, vfxTime, enableTime, disableTime))
        {
            Debug.Log($"[CombatSceneSetup] Attack 动画事件布局已是最新（版本 {EventLayoutVersion}），跳过写入");
            return;
        }

        // 清空后重建，保证重复执行不叠加事件
        clip.events = new[]
        {
            new AnimationEvent
            {
                time = vfxTime,
                functionName = "UpdateAttack",
                intParameter = 1,
                stringParameter = EventLayoutTag,
            },
            new AnimationEvent
            {
                time = enableTime,
                functionName = "EnableHitbox",
                stringParameter = EventLayoutTag,
            },
            new AnimationEvent
            {
                time = disableTime,
                functionName = "DisableHitbox",
                stringParameter = EventLayoutTag,
            },
        };

        importer.clipAnimations = clips;
        EditorUtility.SetDirty(importer);
        importer.SaveAndReimport();

        Debug.Log($"[CombatSceneSetup] Attack 动画事件已写入: 片段 {clip.name} 长度≈{length:0.###}s "
            + $"特效@{length * AttackVfxRatio:0.###}s 开判定@{length * HitboxStartRatio:0.###}s "
            + $"关判定@{length * HitboxEndRatio:0.###}s（占比 {HitboxStartRatio:P0}~{HitboxEndRatio:P0}）");
    }

    // 在实体上搭攻击判定盒：子物体 + 默认关闭的 Trigger BoxCollider + Hitbox 组件
    private static Hitbox EnsureHitbox(GameObject owner, string name, Vector3 center, Vector3 size,
        CampType camp, MonoBehaviour damageSource, bool onRightHand)
    {
        Transform existing = FindChildByName(owner.transform, name);
        if (existing != null && existing.GetComponent<Hitbox>() != null)
        {
            existing.gameObject.SetActive(true);
            return existing.GetComponent<Hitbox>();
        }

        GameObject host = new GameObject(name);
        // 玩家侧挂到手骨下（跟着挥砍走），敌人侧挂在根节点下
        Transform parent = owner.transform;
        if (onRightHand)
        {
            Transform hand = FindChildByName(owner.transform, "mixamorig:RightHand");
            if (hand != null) parent = hand;
            else Debug.LogWarning($"[CombatSceneSetup] 没找到右手骨骼，{name} 改挂在根节点下");
        }

        host.transform.SetParent(parent, false);
        host.transform.localPosition = center;
        host.transform.localRotation = Quaternion.identity;
        // 手骨带了 0.01 的缩放，判定盒尺寸要换算回本地空间才与预期一致
        Vector3 scale = parent.lossyScale;
        host.transform.localScale = new Vector3(
            scale.x != 0f ? 1f / scale.x : 1f,
            scale.y != 0f ? 1f / scale.y : 1f,
            scale.z != 0f ? 1f / scale.z : 1f);

        BoxCollider collider = host.AddComponent<BoxCollider>();
        collider.isTrigger = true;
        collider.size = size;
        // 默认关闭：等动画事件来开，否则一进场就会打到人
        collider.enabled = false;

        Hitbox hitbox = host.AddComponent<Hitbox>();
        hitbox.Configure(camp, damageSource, 1f);
        return hitbox;
    }

    // 给实体挂攻击特效管理器，并把 Hitbox 与特效一起接给战斗组件
    private static AttackVFXManager EnsureAttackVfx(GameObject owner, MonoBehaviour damageSource)
    {
        AttackVFXManager vfx = owner.GetComponent<AttackVFXManager>();
        if (vfx == null) vfx = owner.AddComponent<AttackVFXManager>();
        vfx.Configure(owner.transform, PopupLifetime * 0.25f, 1.9f, 1.1f);
        EditorUtility.SetDirty(vfx);
        return vfx;
    }

    // 清理预制体里的失效组件（脚本被删掉后残留的 MonoBehaviour）。
    // 为什么必须清：只要预制体里还有 m_Script = 0 的组件，Unity 就会拒绝保存它，
    // 于是所有装配改动全部静默不落盘 —— 排查时表现为「工具跑完日志正常，资产却没变」。
    //
    // 只扫**根预制体自身的对象**，绝不碰嵌套预制体实例（剑就是这种实例）：
    // 清理工具作用到嵌套实例上会把它整块摘掉，之前就是这样把剑弄丢的。
    private static int StripMissingScripts(GameObject root, string label)
    {
        int removed = 0;
        foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
        {
            // 跳过属于嵌套预制体实例的节点（含实例根本身）
            if (node != root.transform && PrefabUtility.IsPartOfPrefabInstance(node)) continue;

            removed += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(node.gameObject);
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(node.gameObject);
        }

        if (removed > 0) Debug.Log($"[CombatSceneSetup] {label} 清理失效脚本组件: {removed} 个");
        return removed;
    }

    // 事件是否已经写过：只要有 EnableHitbox + DisableHitbox 就算就位
    private static bool HasHitboxEvents(ModelImporterClipAnimation clip)
    {
        if (clip.events == null || clip.events.Length == 0) return false;
        bool hasEnable = false;
        bool hasDisable = false;
        foreach (AnimationEvent evt in clip.events)
        {
            if (evt.functionName == "EnableHitbox") hasEnable = true;
            if (evt.functionName == "DisableHitbox") hasDisable = true;
        }

        return hasEnable && hasDisable;
    }

    // 事件布局是否就是当前版本：用 stringParameter 存版本标记 + 比对时间。
    // 改布局时把 EventLayoutVersion 加一即可触发重写。
    private static bool IsEventLayoutCurrent(ModelImporterClipAnimation clip,
        float vfxTime, float enableTime, float disableTime)
    {
        float tolerance = 0.002f;
        foreach (AnimationEvent evt in clip.events)
        {
            if (evt.stringParameter != EventLayoutTag) return false;
            if (evt.functionName == "UpdateAttack" && Mathf.Abs(evt.time - vfxTime) > tolerance) return false;
            if (evt.functionName == "EnableHitbox" && Mathf.Abs(evt.time - enableTime) > tolerance) return false;
            if (evt.functionName == "DisableHitbox" && Mathf.Abs(evt.time - disableTime) > tolerance) return false;
        }

        return true;
    }

    private static AnimationClip LoadClipAfterImport(string fbxPath)
    {
        return AssetDatabase.LoadAllAssetsAtPath(fbxPath)
            .OfType<AnimationClip>()
            .FirstOrDefault(candidate => !candidate.name.StartsWith("__preview__"));
    }

    // 量出动画里「动作真正结束」的时间（最后一次显著变化的时刻）。
    //
    // 为什么需要它：Mixamo 的片段常带长尾 —— 本工程的 Attack 总长 3.782s，
    // 但动作在 1.4s 就结束了，后面 2.382s 是静止/缓慢归位。
    // 攻击窗口若按片段全长设置，玩家会被白锁 2.4 秒打不出第二刀（表现就是「攻击完卡住」）。
    private static float MeasureMotionEnd(AnimationClip clip)
    {
        if (clip == null) return 0f;

        AnimationCurve[] curves = AnimationUtility.GetCurveBindings(clip)
            .Select(binding => AnimationUtility.GetEditorCurve(clip, binding))
            .Where(curve => curve != null && curve.length > 1)
            .ToArray();

        float motionEnd = 0f;
        foreach (AnimationCurve curve in curves)
        {
            Keyframe[] keys = curve.keys;
            for (int i = 1; i < keys.Length; i++)
            {
                // 阈值 0.5（旋转度数 / 位移厘米量级）：低于这个值肉眼看不出来
                if (Mathf.Abs(keys[i].value - keys[i - 1].value) > 0.5f && keys[i].time > motionEnd)
                {
                    motionEnd = keys[i].time;
                }
            }
        }

        return motionEnd > 0f ? motionEnd : clip.length;
    }

    // 量出主片段时长。写入动画事件会改变 fbx 的导入设置，这里列出所有片段便于核对。
    private static float MeasureClipLength(string fbxPath)
    {
        AnimationClip[] clips = AssetDatabase.LoadAllAssetsAtPath(fbxPath)
            .OfType<AnimationClip>()
            .Where(candidate => !candidate.name.StartsWith("__preview__"))
            .ToArray();
        if (clips.Length == 0)
        {
            Debug.LogWarning($"[CombatSceneSetup] 未能从 {fbxPath} 取到动画片段");
            return 0f;
        }

        string roster = string.Join(" | ", System.Array.ConvertAll(clips,
            c => $"{c.name}:{c.length:0.###}s"));
        Debug.Log($"[CombatSceneSetup] {System.IO.Path.GetFileName(fbxPath)} 片段共 {clips.Length} 个: {roster}");
        return clips[0].length;
    }

    // 受击反馈参数只能通过 SerializedObject 写：它们是 private 序列化字段，
    // 场景里已存过一份旧值，光改 C# 默认值不会生效（实测跳字存活一直停在 0.7s 就是这个原因）。
    private static void ApplyDamageFeedback(Damageable damageable)
    {
        SerializedObject serialized = new SerializedObject(damageable);
        SetIfExists(serialized, "_numberLifetime", PopupLifetime);
        SetIfExists(serialized, "_riseSpeed", PopupRiseSpeed);
        SetIfExists(serialized, "_shakeAmplitude", EnemyShakeAmplitude);
        SetIfExists(serialized, "_invulnerableTime", EnemyInvulnerableTime);
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(damageable);
    }

    private static void SetIfExists(SerializedObject serialized, string field, float value)
    {
        SerializedProperty property = serialized.FindProperty(field);
        if (property == null)
        {
            Debug.LogWarning($"[CombatSceneSetup] 序列化字段不存在，跳过: {field}");
            return;
        }

        bool changed = Mathf.Abs(property.floatValue - value) > 0.0001f;
        property.floatValue = value;
        if (changed) Debug.Log($"[CombatSceneSetup] 写入序列化字段 {field}: {value}");
    }

    private static Bounds CalculateBounds(GameObject target)
    {
        Renderer[] renderers = target.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            Debug.LogWarning($"[CombatSceneSetup] {target.name} 没有 Renderer，碰撞体尺寸回退为默认值");
            return new Bounds(target.transform.position + Vector3.up, new Vector3(1f, 2f, 1f));
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        return bounds;
    }

    private static GameObject FindInScene(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Transform found = FindChildByName(root.transform, name);
            if (found != null) return found.gameObject;
        }

        return null;
    }

    private static Transform FindChildByName(Transform root, string name)
    {
        if (string.Equals(root.name, name, System.StringComparison.OrdinalIgnoreCase)) return root;
        foreach (Transform child in root)
        {
            Transform found = FindChildByName(child, name);
            if (found != null) return found;
        }

        return null;
    }

    // 只读诊断：查清攻击片段在 fbx 里的真实名字。动画片段名依赖导入设置，
    // 猜名字会在换 fbx 时静默失败，这里一次把事实打出来。
    // 批量模式可用：-executeMethod CombatSceneSetup.DiagnoseAttackClip
    public static void DiagnoseAttackClip()
    {
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath("Assets/Animations/fbx/Attack.fbx");
        List<string> names = new List<string>();
        foreach (Object asset in assets)
        {
            string kind = asset is AnimationClip ? "Clip" : asset.GetType().Name;
            names.Add($"{kind}:{asset.name}");
        }

        Debug.Log("[CombatDiag] Attack.fbx 内部资源: " + string.Join(" | ", names));

        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPathForDiagnostics);
        if (controller == null)
        {
            Debug.LogError("[CombatDiag] 找不到 player.controller");
            return;
        }

        Debug.Log($"[CombatDiag] player.controller 参数数: {controller.parameters.Length} 状态数: "
            + (controller.layers.Length > 0 ? controller.layers[0].stateMachine.states.Length : -1));
        if (controller.layers.Length == 0) return;
        for (int i = 0; i < controller.layers[0].stateMachine.states.Length; i++)
        {
            var child = controller.layers[0].stateMachine.states[i];
            Debug.Log($"[CombatDiag]   状态[{i}] {child.state.name} 片段={(child.state.motion != null ? child.state.motion.name : "空")}");
        }
    }

    private const string ControllerPathForDiagnostics = "Assets/Animations/player.controller";

    [MenuItem("超高速行者/检查战斗装配")]
    public static void Verify()
    {
        List<string> lines = new List<string>();

        GameObject player = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
        PlayerCombat combat = player != null ? player.GetComponent<PlayerCombat>() : null;
        SwordBinder binder = player != null ? player.GetComponent<SwordBinder>() : null;
        lines.Add("Player.prefab 有 PlayerCombat: " + (combat != null));
        lines.Add("Player.prefab 有 Player 身份组件: " + (player != null && player.GetComponent<Player>() != null));
        PlayerHealth playerHealth = player != null ? player.GetComponent<PlayerHealth>() : null;
        lines.Add("Player.prefab 有 PlayerHealth: " + (playerHealth != null)
            + (playerHealth != null ? $"（上限 {playerHealth.MaxHealth}）" : ""));
        lines.Add("Player.prefab 有 SwordBinder: " + (binder != null));
        lines.Add("剑引用已接: " + (binder != null && binder.Sword != null));
        lines.Add("攻击参数 dmg/range/duration: " + (combat != null
            ? $"{combat.AttackDamage}/{combat.AttackRange}/{combat.AttackDuration}"
            : "无"));
        lines.Add("命中减速: 冲击 " + (combat != null ? $"{combat.ImpactTimeScale:0.##}x/{combat.ImpactSeconds:0.##}s" : "无")
            + " → 尾巴 " + (combat != null ? $"{combat.HitTimeScale:0.##}x/{combat.HitStopSeconds:0.##}s" : "无"));

        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        lines.Add("场景有 TimeManager: " + (Object.FindObjectOfType<TimeManager>() != null));
        lines.Add("相机有 CameraShaker: " + (Camera.main != null && Camera.main.GetComponent<CameraShaker>() != null));

        GameObject enemy = FindInScene(scene, EnemyName);
        Enemy enemyComponent = enemy != null ? enemy.GetComponent<Enemy>() : null;
        lines.Add($"{EnemyName} 有 Enemy 组件: " + (enemyComponent != null));
        lines.Add($"{EnemyName} 有 CharacterController: " + (enemy != null && enemy.GetComponent<CharacterController>() != null));
        lines.Add($"{EnemyName} 数值 maxHealth/attack/speed: " + (enemyComponent != null
            ? $"{enemyComponent.MaxHealth}/{enemyComponent.AttackPower}/{enemyComponent.MoveSpeed}"
            : "无"));

        foreach (string line in lines) Debug.Log("[CombatVerify] " + line);
    }
}
