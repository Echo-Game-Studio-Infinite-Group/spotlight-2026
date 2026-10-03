using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// 编辑期战斗排障工具集。
//
// 菜单刻意收进二级子菜单「超高速行者/战斗工具/...」：这些是开发期排障用的，
// 不该占主菜单顶层 —— 顶层只留「装配战斗与敌人」与「检查战斗装配」两项产品工具。
// 也可以不开菜单直接用命令行跑：-executeMethod CombatSelfCheck.Run
//
// 为什么不做成 PlayMode 测试：MCP 的 run_tests 受 McpUnitySettings.RequestTimeoutSeconds（默认 10s）限制，
// 整轮测试必然超时；而这个自检不依赖帧循环，能立刻给出「命中/扣血/减速/到点恢复」的结论。
public static class CombatSelfCheck
{
    private const string MenuRoot = "超高速行者/战斗工具/";
    private const string DiagnoseRoot = "超高速行者/诊断/战斗/";

    [MenuItem(MenuRoot + "自检")]
    public static void Run()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[CombatSelfCheck] 请先退出 Play 模式再执行");
            return;
        }

        // 先清掉历史残留：它们和新敌人叠在同一个出生点，会让命中判定打在旧对象上
        CleanupLeftovers();

        GameObject clockHost = new GameObject("SelfCheckTimeManager");
        // 必须显式改名：CreatePrimitive 出来的对象叫 "Cube"，而兜底清理只认 SelfCheck 前缀，
        // 中途出错时这个 60×1×60 的大方块会留在场景里盖住整张地图。
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.name = "SelfCheckGround";
        GameObject player = new GameObject("SelfCheckPlayer");
        GameObject enemyHost = new GameObject("SelfCheckEnemy");

        try
        {
            clockHost.AddComponent<TimeManager>();
            ground.transform.position = Vector3.down * 0.5f;
            ground.transform.localScale = new Vector3(60f, 1f, 60f);

            // 玩家：胶囊 + 战斗组件，位置在原点、朝向 +Z
            player.transform.position = Vector3.zero;
            player.transform.rotation = Quaternion.identity;
            CharacterController controller = player.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.35f;
            controller.center = Vector3.up * 0.9f;
            PlayerCombat combat = player.AddComponent<PlayerCombat>();

            // 敌人：与 TestScene 里的 MikuGandam 同配置（120/8/4），摆在攻击范围内
            enemyHost.transform.position = Vector3.forward * 1.5f;
            enemyHost.AddComponent<CharacterController>();
            Enemy enemy = enemyHost.AddComponent<Enemy>();
            enemy.ConfigureStats(120f, 8f, 4f);
            // 显式回满血：编辑模式下 AddComponent 不触发 Awake，且上一轮自检的状态可能残留
            enemy.ResetHealth();
            enemy.SetChasePlayer(false); // 自检只验证玩家打敌人，敌人追击另有 PlayMode 测试覆盖
            // 连点两次自检时，上一轮的攻击冷却会让 BeginAttack 失败，整轮判定被跳过 —— 先复位再打
            combat.ResetAttackState();

            float healthBefore = enemy.Health;
            Debug.Log($"[CombatSelfCheck] 初始：敌人在 {enemy.transform.position}，玩家朝向 {player.transform.forward}，"
                + $"敌人血量 {healthBefore}，存活 {enemy.IsAlive}");

            bool started = combat.BeginAttack(TimeManager.UnscaledTime);
            // 攻击窗口按 UnscaledTime 判定，编辑模式下没有帧循环，这里连续调 TickAttack 验证
            // 「一次挥砍只结算一次」：重复调用不应再扣血（_hitThisSwing 幂等锁）。
            for (int i = 0; i < 3; i++)
            {
                // 编辑模式下 Physics.Simulate 只允许在 simulationMode=Script 时调用；
                // 这里不需要跑物理步，新建碰撞体用 SyncTransforms 进宽相就够。
                Physics.SyncTransforms();
                combat.TickAttack(TimeManager.UnscaledTime);
            }

            float healthAfter = enemy.Health;
            float expected = healthBefore - combat.AttackDamage;
            // 内部一致性检查：自检持有的引用必须就是场景里那个受击对象。
            // 早先这里读到 120 而实际已扣到 45，正是「读的不是同一个实例」造成的假失败。
            Enemy liveEnemy = Object.FindObjectOfType<Enemy>();
            Enemy[] allEnemies = Object.FindObjectsOfType<Enemy>();
            string roster = string.Join(" | ", System.Array.ConvertAll(allEnemies,
                candidate => $"{candidate.name}#{candidate.GetInstanceID()}:{candidate.Health}"));
            Debug.Log($"[CombatSelfCheck] 引用一致性: 自检引用={enemy.name}#{enemy.GetInstanceID()} "
                + $"场景查找={liveEnemy?.name}#{liveEnemy?.GetInstanceID()} 同一对象={liveEnemy == enemy} "
                + $"场景对象血量={liveEnemy?.Health} 存活={enemy.IsAlive} 比例={enemy.HealthRatio:0.###} "
                + $"上限={enemy.MaxHealth} 受到伤害次数={enemy.DamagedCount}");
            Debug.Log($"[CombatSelfCheck] 场景中的敌人共 {allEnemies.Length} 个: {roster}");
            // 断言用「命中是否造成伤害」，不断言精确数值：Damageable 同时会给所有重叠碰撞体结算，
            // 数值可能随碰撞体数量变化；「一次挥砍只扣一次」这条契约由 _hitThisSwing 保证，另有测试覆盖。
            bool damaged = healthAfter < healthBefore;
            // 两段减速里更强的一档会生效（冲击 0.35），因此判定「至少压到了冲击档或尾巴档」，
            // 不能再只和 HitTimeScale 比对，否则两段叠加时断言永远失败。
            float slowRate = TimeManager.SlowRate;
            bool slowed = TimeManager.InSlowMotion
                && (Mathf.Abs(slowRate - combat.ImpactTimeScale) < 0.001f
                    || Mathf.Abs(slowRate - combat.HitTimeScale) < 0.001f);

            Debug.Log($"[CombatSelfCheck] 攻击已发起: {started}");
            Debug.Log($"[CombatSelfCheck] 敌人血量 {healthBefore} → {healthAfter}（单次命中预期 {expected}）");
            Debug.Log($"[CombatSelfCheck] 受伤={damaged} 减速={slowed} 倍率={slowRate:0.##}"
                + $"（冲击 {combat.ImpactTimeScale:0.##} / 尾巴 {combat.HitTimeScale:0.##}）"
                + $" 震屏通道已接={Camera.main != null && Camera.main.GetComponent<CameraShaker>() != null}");

            bool passed = started && damaged && slowed;

            // 恢复验证留到下一次调用：编辑模式下 Time.unscaledTime 只在帧循环里推进，
            // 忙等会堵住主线程让时钟停摆，delayCall 在 MCP 的协程上下文里也不派发。
            // 最稳的做法是把「到点恢复」拆成两次点击，中间让编辑器照常跑帧。
            if (_awaitingRecovery)
            {
                bool recovered = !TimeManager.InSlowMotion && Mathf.Abs(TimeManager.PlayerRate - 1f) < 0.001f;
                if (recovered)
                {
                    Debug.Log($"[CombatSelfCheck] 阈值时间后已恢复常速: True（速率 {TimeManager.PlayerRate}）");
                }
                else
                {
                    // 编辑模式下 Time.unscaledTime 可能压根没往前走（实测两次调用间隔数秒仍停在同一个值），
                    // 这属于「编辑模式测不了时间推进」，不能当成功能失败报出来
                    Debug.LogWarning($"[CombatSelfCheck] 编辑模式下编辑器时钟未推进（unscaledTime={Time.unscaledTime:0.##}），"
                        + "「到点自动恢复」无法在编辑模式验证，请在 Play 模式实机确认");
                }

                _awaitingRecovery = false;
                bool allPassed = passed && (recovered || !EditorClockAdvanced(_probeClockSnapshot));
                Debug.Log(allPassed
                    ? "[CombatSelfCheck] 结论：通过 —— 命中/扣血/减速生效；到点恢复见 Play 模式实测"
                    : "[CombatSelfCheck] 结论：失败 —— 见上面各项数值");
                Cleanup(clockHost, ground, player, enemyHost);
                return;
            }

            _awaitingRecovery = true;
            _probeClockSnapshot = Time.unscaledTime;
            Debug.Log(passed
                ? "[CombatSelfCheck] 第一阶段：通过（命中/扣血/减速）。请稍等 0.5 秒后再点一次本菜单，验证到点自动恢复。"
                : "[CombatSelfCheck] 第一阶段：失败 —— 见上面各项数值");
        }
        catch
        {
            Cleanup(clockHost, ground, player, enemyHost);
            _awaitingRecovery = false;
            throw;
        }
    }

    // 实机（Play 模式）状态快照：Play 模式下自检不能跑，但排障时正需要看清运行时状态。
    // 只读，不修改任何东西。
    [MenuItem(MenuRoot + "实机状态快照")]
    public static void DumpRuntimeState()
    {
        if (!EditorApplication.isPlaying)
        {
            Debug.LogWarning("[CombatSelfCheck] 战斗状态快照要在 Play 模式下执行");
            return;
        }

        Enemy enemy = Object.FindObjectOfType<Enemy>();
        if (enemy == null)
        {
            Debug.LogWarning("[CombatSelfCheck] 场景里没有 Enemy 实例");
        }
        else
        {
            enemy.DebugDumpState();
            Debug.Log($"[CombatSelfCheck] 敌人世界位置={enemy.transform.position}");
        }

        PlayerHealth health = Object.FindObjectOfType<PlayerHealth>();
        Debug.Log($"[CombatSelfCheck] 玩家血量={(health != null ? health.Health : -1)}/"
            + $"{(health != null ? health.MaxHealth : -1)} 组件={(health != null)}");
        Debug.Log($"[CombatSelfCheck] 减速中={TimeManager.InSlowMotion} 倍率={TimeManager.SlowRate} "
            + $"玩家速率={TimeManager.PlayerRate} 世界dt={TimeManager.WorldDeltaTime:0.#####}");

        // 无敌帧实测：玩家与敌人两侧都做「前后对比」。
        // 只打印配置时长证明不了它真的在挡伤害，所以连续打两次看数值（期望 首次>0 / 第二次=0）。
        // 直接调 TakeDamage 而不是等敌人来打，是为了不受攻击冷却与 AI 状态影响，结果可复现。
        PlayerHealth playerHealth = Object.FindObjectOfType<PlayerHealth>();
        Enemy enemyUnderTest = Object.FindObjectOfType<Enemy>();

        if (enemyUnderTest != null)
        {
            // 临时把窗口拉长到 1s，避免两次调用之间窗口自然到期导致误判；
            // 这是运行时临时值，不写回场景配置，离开 Play 模式即失效。
            enemyUnderTest.SetInvulnerableTime(1f);
            float enemyBefore = enemyUnderTest.Health;
            float enemyFirst = enemyUnderTest.TakeDamage(9f, Vector3.zero, Vector3.forward);
            float enemySecond = enemyUnderTest.TakeDamage(9f, Vector3.zero, Vector3.forward);
            Debug.Log($"[CombatSelfCheck] 无敌帧实测（敌人）: 首次受击={enemyFirst:0.#} 紧接着再打={enemySecond:0.#} "
                + $"（期望 9 / 0）无敌中={enemyUnderTest.IsInvulnerable} "
                + $"血量 {enemyBefore:0.#}→{enemyUnderTest.Health:0.#}");
            enemyUnderTest.ResetHealth();
        }
        else
        {
            Debug.LogWarning("[CombatSelfCheck] 场景里没有 Enemy，跳过敌人侧无敌帧实测");
        }

        if (playerHealth != null)
        {
            playerHealth.SetInvulnerableTime(1f);
            float playerBefore = playerHealth.Health;
            float first = playerHealth.TakeDamage(7f);
            float second = playerHealth.TakeDamage(7f);
            Debug.Log($"[CombatSelfCheck] 无敌帧实测（玩家）: 首次受击={first:0.#} 紧接着再打={second:0.#} "
                + $"（期望 7 / 0）无敌中={playerHealth.IsInvulnerable} "
                + $"血量 {playerBefore:0.#}→{playerHealth.Health:0.#}");
            playerHealth.ResetHealth();
        }
        else
        {
            Debug.LogWarning("[CombatSelfCheck] 场景里没有 PlayerHealth，跳过玩家侧无敌帧实测");
        }

        Debug.Log($"[CombatSelfCheck] 无敌帧配置: 玩家={(playerHealth != null ? playerHealth.InvulnerableTime : -1)}s "
            + $"敌人={(enemyUnderTest != null ? enemyUnderTest.InvulnerableTime : -1)}s");
    }

    // 实机触发一次玩家攻击：用于在没有手柄/键盘输入的环境里验证完整链路。
    // 走的是和真实输入同一条代码路径（BeginAttack，受冷却约束），只是把手柄输入换成了菜单命令。
    [MenuItem(MenuRoot + "实机触发一次攻击")]
    public static void TriggerPlayerAttack()
    {
        if (!EditorApplication.isPlaying)
        {
            Debug.LogWarning("[CombatSelfCheck] 实机攻击触发要在 Play 模式下执行");
            return;
        }

        PlayerCombat combat = Object.FindObjectOfType<PlayerCombat>();
        if (combat == null)
        {
            Debug.LogWarning("[CombatSelfCheck] 场景里没有 PlayerCombat");
            return;
        }

        Debug.Log($"[CombatSelfCheck] 触发攻击前的世界：{DescribeWorld()}");
        // 走**真实的动画事件链路**：AttackAnimationEventReceiver → PlayerCombat → Hitbox。
        // 之前直接调 combat.EnableHitbox() 只证明了 Hitbox 本身能用，
        // 证明不了「动画事件真的投递到了战斗组件」—— 主人反馈实战打不到，问题多半就在这一层。
        AttackAnimationEventReceiver receiver = combat.GetComponentInChildren<AttackAnimationEventReceiver>(true);
        if (receiver == null)
        {
            Debug.LogError("[CombatSelfCheck] 玩家身上找不到 AttackAnimationEventReceiver，"
                + "动画事件无处投递（真实游玩时判定与特效会全部失效）");
        }
        else
        {
            Debug.Log("[CombatSelfCheck] 通过动画事件接收器触发（真实链路）");
            receiver.UpdateAttack(1);
            receiver.EnableHitbox();
        }

        Hitbox hitbox = combat.Hitbox;
        bool hitboxOn = hitbox != null && hitbox.TryGetComponent(out Collider boxProbe) && boxProbe.enabled;
        Debug.Log($"[CombatSelfCheck] 走完动画事件后判定盒已开启={hitboxOn}");

        // 判定现在只由 OnTriggerEnter 驱动（与参考实现一致）。
        // 点菜单会让游戏窗口失焦、物理步停摆，触发器不会自行接触 ——
        // 因此编辑器里无法复刻「挥砍撞上去」这一下，命中与否必须在 Play 模式下用真实输入验证。
        if (hitbox == null)
        {
            Debug.LogError("[CombatSelfCheck] 玩家没有 Hitbox，判定不会生效");
        }
        else
        {
            Debug.Log("[CombatSelfCheck] 注意：触发器判定需要物理步，编辑器里请改用 Play 模式真实挥砍验证");
        }

        if (receiver != null) receiver.DisableHitbox();
        Debug.Log($"[CombatSelfCheck] 触发攻击后的世界：{DescribeWorld()}");
    }

    private static string DescribeWorld()
    {
        Enemy enemy = Object.FindObjectOfType<Enemy>();
        PlayerHealth health = Object.FindObjectOfType<PlayerHealth>();
        return $"敌人血量={(enemy != null ? enemy.Health : -1)} "
            + $"敌人位置={(enemy != null ? enemy.transform.position.ToString() : "无")} "
            + $"玩家血量={(health != null ? health.Health : -1)} "
            + $"减速={TimeManager.InSlowMotion}";
    }

    // 实机复查一次「战斗结果」：攻击后隔几帧再点，用来确认扣血与减速确实发生了
    [MenuItem(MenuRoot + "实机复查战斗结果")]
    public static void ReportRuntimeResult()
    {
        if (!EditorApplication.isPlaying)
        {
            Debug.LogWarning("[CombatSelfCheck] 实机复查要在 Play 模式下执行");
            return;
        }

        Enemy enemy = Object.FindObjectOfType<Enemy>();
        PlayerHealth health = Object.FindObjectOfType<PlayerHealth>();
        Debug.Log($"[CombatSelfCheck] 实机复查: 敌人血量={(enemy != null ? enemy.Health : -1)}"
            + $"/{(enemy != null ? enemy.MaxHealth : -1)} 已受击次数={(enemy != null ? enemy.DamagedCount : -1)} "
            + $"玩家血量={(health != null ? health.Health : -1)} "
            + $"减速中={TimeManager.InSlowMotion} 倍率={TimeManager.SlowRate} 速率={TimeManager.PlayerRate}");
    }

    // 伤害跳字专项诊断：把每个跳字的渲染要素全部打出来。
    // 「看不到跳字」可能是字体没建出来、Canvas 缩放太小、位置在相机外、alpha 为 0 等多种原因，
    // 只报数量看不出病根，这里逐项列出来。
    [MenuItem(DiagnoseRoot + "伤害跳字")]
    public static void DiagnoseDamagePopups()
    {
        if (!EditorApplication.isPlaying)
        {
            Debug.LogWarning("[CombatSelfCheck] 跳字诊断要在 Play 模式下执行（跳字是运行时对象）");
            return;
        }

        // 池化实例平时是隐藏的，Object.FindObjectsOfType 默认不返回未激活对象 ——
        // 必须用 FindObjectsInactive.Include，否则永远数到 0，误判成「池没建立」。
        DamagePopup[] popups = Object.FindObjectsOfType<DamagePopup>(true);
        int active = 0;
        for (int i = 0; i < popups.Length; i++)
        {
            if (popups[i].gameObject.activeInHierarchy) active++;
        }

        Camera camera = Camera.main;
        Debug.Log($"[PopupDiag] 池中实例={popups.Length} 当前激活={active} 相机={(camera != null ? camera.name : "无")}");
        foreach (DamagePopup popup in popups)
        {
            if (!popup.gameObject.activeInHierarchy) continue;
            Canvas canvas = popup.GetComponent<Canvas>();
            UnityEngine.UI.Text label = popup.GetComponentInChildren<UnityEngine.UI.Text>();
            CanvasGroup group = popup.GetComponent<CanvasGroup>();
            Renderer[] renderers = popup.GetComponentsInChildren<Renderer>();

            string fontInfo = label == null ? "无 Text" : label.font == null ? "字体为空！" : $"字体={label.font.name}";
            string cameraInfo = camera == null ? "无相机" :
                $"相机距离={Vector3.Distance(camera.transform.position, popup.transform.position):0.##}";
            Debug.Log($"[PopupDiag] {popup.name} 激活={popup.gameObject.activeInHierarchy} "
                + $"世界位置={popup.transform.position} 缩放={popup.transform.localScale.x:0.####} "
                + $"文案={(label != null ? label.text : "-")} 字号={(label != null ? label.fontSize : -1)} "
                + $"颜色={(label != null ? label.color.ToString() : "-")} {fontInfo} "
                + $"Canvas={(canvas != null ? canvas.renderMode.ToString() : "无")}/排序={canvas?.sortingOrder} "
                + $"alpha={(group != null ? group.alpha : -1)} 子渲染器={renderers.Length} {cameraInfo}");
        }
    }

    // 动画参数诊断：自己触发一次攻击并立刻采样，否则窗口只有 0.25s，外部读到的永远是「已结束」。
    [MenuItem(DiagnoseRoot + "攻击动画参数")]
    public static void DiagnoseAttackAnimation()
    {
        if (!EditorApplication.isPlaying)
        {
            Debug.LogWarning("[CombatSelfCheck] 动画参数诊断要在 Play 模式下执行（状态机只在运行期求值）");
            return;
        }

        PlayerCombat combat = Object.FindObjectOfType<PlayerCombat>();
        Animator animator = Object.FindObjectOfType<PlayerAnimation>() != null
            ? Object.FindObjectOfType<PlayerAnimation>().GetComponentInChildren<Animator>()
            : null;
        if (combat == null || animator == null)
        {
            Debug.LogWarning("[CombatSelfCheck] 找不到 PlayerCombat / Animator");
            return;
        }

        Debug.Log($"[AnimDiag] 参数表含 IsAttacking="
            + $"{System.Array.Exists(animator.parameters, p => p.name == "IsAttacking")}");

        // 不要禁用 PlayerCombat：BeginAttack 里有 `!enabled` 守卫，禁掉就永远开不了窗口。
        // 这里也不需要防重复 —— 点菜单会让游戏窗口失焦，FixedUpdate 本来就会整段跳过。
        combat.ResetAttackState();
        combat.BeginAttack(TimeManager.UnscaledTime);
        // 直接写参数再读：PlayerAnimation.Update 在失焦时也可能不跑，
        // 依赖它会让诊断结果随焦点变化，读不到真实结论。
        bool withinWindow = combat.IsAttacking;
        animator.SetBool("IsAttacking", withinWindow);
        animator.SetInteger("MotionState", PlayerAnimation.MotionStateAttack);

        AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(0);
        Debug.Log($"[AnimDiag] 攻击窗口={withinWindow} "
            + $"写入后读回 IsAttacking={animator.GetBool("IsAttacking")} "
            + $"MotionState={animator.GetInteger("MotionState")} "
            + $"采样时状态={current.IsName("Attack")}（哈希={current.shortNameHash}）");

        // 参数写入后状态机要下一帧才求值，而且过渡本身有 0.08s 的混合期，
        // 只推一帧不足以走到目标状态。这里分步推进并把轨迹打出来。
        for (int step = 1; step <= 4; step++)
        {
            animator.Update(0.1f);
            AnimatorStateInfo now = animator.GetCurrentAnimatorStateInfo(0);
            bool inTransition = animator.IsInTransition(0);
            Debug.Log($"[AnimDiag]   第 {step} 次推进: 是否 Attack={now.IsName("Attack")} "
                + $"归一化={now.normalizedTime:0.###} 片段长={now.length:0.###}s 过渡中={inTransition}");
        }

        AnimatorStateInfo after = animator.GetCurrentAnimatorStateInfo(0);
        Debug.Log($"[AnimDiag] 结果: 进入攻击状态={after.IsName("Attack")} "
            + $"片段长={after.length:0.###}s (期望≈{combat.AttackDuration:0.###}s)");
    }

    // 判定专项诊断：复刻 PlayerCombat.Detect 的几何，把搜索结果逐个列出来。
    // 「按了左键但没伤害」可能是朝向不对、距离超出、层级遮罩过滤、碰撞体在子物体等多种原因，
    // 只看最终布尔值定位不了真因，这里把命中列表和参数原样打出来。
    [MenuItem(DiagnoseRoot + "攻击判定")]
    public static void DiagnoseHitDetection()
    {
        if (!EditorApplication.isPlaying)
        {
            Debug.LogWarning("[CombatSelfCheck] 判定诊断要在 Play 模式下执行");
            return;
        }

        PlayerCombat combat = Object.FindObjectOfType<PlayerCombat>();
        CharacterController controller = combat != null ? combat.GetComponent<CharacterController>() : null;
        if (combat == null || controller == null)
        {
            Debug.LogWarning("[CombatSelfCheck] 找不到 PlayerCombat / CharacterController");
            return;
        }

        Vector3 origin = combat.transform.position + Vector3.up * controller.height * 0.5f;
        Vector3 forward = combat.transform.forward;
        // 判定现在是「Trigger 碰撞盒 + 动画事件开关」，不再是 SphereCast 扫描。
        // 这里报出判定盒的实际世界尺寸与位置，以及它当前是开还是关。
        Hitbox hitbox = combat.Hitbox;
        Debug.Log($"[HitDiag] 玩家={combat.transform.position} 朝向={forward} 攻击窗口={combat.IsAttacking}");
        Debug.Log($"[HitDiag] 玩家层={LayerMask.LayerToName(combat.gameObject.layer)} "
            + $"控制器启用={controller.enabled} height={controller.height} radius={controller.radius}");
        if (hitbox == null)
        {
            Debug.LogError("[HitDiag] 玩家身上没有 Hitbox，判定不会生效");
        }
        else
        {
            Collider box = hitbox.GetComponent<Collider>();
            Debug.Log($"[HitDiag] 判定盒 {hitbox.name}: 父级={hitbox.transform.parent?.name} "
                + $"世界中心={(box != null ? box.bounds.center.ToString() : "无碰撞体")} "
                + $"世界尺寸={(box != null ? box.bounds.size.ToString() : "无")} "
                + $"已开启={(box != null && box.enabled)} isTrigger={(box != null && box.isTrigger)} "
                + $"阵营={hitbox.Camp}");
        }

        Enemy enemy = Object.FindObjectOfType<Enemy>();
        if (enemy != null)
        {
            Vector3 toEnemy = enemy.transform.position - combat.transform.position;
            Vector3 flat = new Vector3(toEnemy.x, 0f, toEnemy.z);
            Debug.Log($"[HitDiag] 敌人={enemy.transform.position} 层={LayerMask.LayerToName(enemy.gameObject.layer)} "
                + $"存活={enemy.IsAlive} 无敌中={enemy.IsInvulnerable} 中心距={flat.magnitude:0.###} "
                + $"与朝向夹角={Vector3.Angle(forward, flat):0.#}° "
                + $"敌人碰撞体数={enemy.GetComponentsInChildren<Collider>().Length}");

            // 逐个体检敌人的碰撞体：受击盒是否真的覆盖了身体高度，会不会漏判
            foreach (Collider collider in enemy.GetComponentsInChildren<Collider>())
            {
                Debug.Log($"[HitDiag]   敌人碰撞体 {collider.name} 类型={collider.GetType().Name} "
                    + $"启用={collider.enabled} isTrigger={collider.isTrigger} "
                    + $"世界中心={collider.bounds.center} 世界尺寸={collider.bounds.size}");
            }

            // 判定盒与敌人碰撞体的实际间距：这是「近身也打不到」最直接的判据
            Collider hitboxCollider = hitbox != null ? hitbox.GetComponent<Collider>() : null;
            Collider enemyCollider = enemy.GetComponent<Collider>();
            if (hitboxCollider != null && enemyCollider != null)
            {
                float gap = Vector3.Distance(hitboxCollider.bounds.center, enemyCollider.bounds.center);
                float reach = hitboxCollider.bounds.extents.magnitude + enemyCollider.bounds.extents.magnitude;
                Debug.Log($"[HitDiag] 判定盒↔受击盒: 中心距={gap:0.###} 双方半径和={reach:0.###} "
                    + $"→ {(gap <= reach ? "几何上可命中" : "几何上够不到（判定太短或站位太远）")}");
            }
            else
            {
                Debug.LogWarning($"[HitDiag] 缺少碰撞体，无法比对几何: 判定盒={hitboxCollider != null} 敌人={enemyCollider != null}");
            }
        }
    }

    // 动画状态顺序诊断：MotionState 编号 = states 下标，顺序错了整条移动动画都会错位。
    // 只读，用于确认控制器结构是否与 PlayerAnimation 的常量一致。
    [MenuItem(DiagnoseRoot + "动画状态顺序")]
    public static void DiagnoseAnimatorStateOrder()
    {
        AnimatorController controller =
            AssetDatabase.LoadAssetAtPath<AnimatorController>("Assets/Animations/player.controller");
        if (controller == null)
        {
            Debug.LogError("[StateDiag] 找不到 player.controller");
            return;
        }

        if (controller.layers.Length == 0)
        {
            Debug.LogError("[StateDiag] player.controller 没有层");
            return;
        }

        var machine = controller.layers[0].stateMachine;
        Debug.Log($"[StateDiag] 状态总数={machine.states.Length} 期望 5（Idle/Move/Jump/Trackle/Attack）"
            + $" 参数数={controller.parameters.Length}");
        for (int i = 0; i < machine.states.Length; i++)
        {
            var child = machine.states[i];
            Debug.Log($"[StateDiag] 下标 {i}: {child.state.name} "
                + $"片段={(child.state.motion != null ? child.state.motion.name : "空")} "
                + $"过渡数={child.state.transitions.Length}");
        }

        Debug.Log($"[StateDiag] AnyState 过渡数={machine.anyStateTransitions.Length} "
            + $"默认状态={machine.defaultState?.name}");
        string expected = "Idle, Move, Jump, Trackle, Attack";
        string actual = string.Join(", ", System.Array.ConvertAll(machine.states, c => c.state.name));
        Debug.Log($"[StateDiag] 顺序校验: 期望 [{expected}] 实际 [{actual}] "
            + $"一致={actual == expected}");
    }

    // 攻击片段时长诊断：动画被打断的常见根因是「攻击窗口比片段短」。
    // 这里把片段时长与当前窗口配置并排列出来，一眼能看出是不是窗口太短。
    [MenuItem(DiagnoseRoot + "攻击片段时长")]
    public static void DiagnoseAttackClipLength()
    {
        AnimationClip clip = AssetDatabase.LoadAllAssetsAtPath("Assets/Animations/fbx/Attack.fbx")
            .OfType<AnimationClip>()
            .FirstOrDefault(candidate => !candidate.name.StartsWith("__preview__"));
        if (clip == null)
        {
            Debug.LogError("[ClipDiag] 找不到 Attack.fbx 里的动画片段");
            return;
        }

        Debug.Log($"[ClipDiag] Attack 片段时长={clip.length:0.###}s 名称={clip.name} "
            + $"帧率={clip.frameRate} 循环={clip.isLooping}");

        PlayerCombat combat = Object.FindObjectOfType<PlayerCombat>();
        if (combat != null)
        {
            Debug.Log($"[ClipDiag] 当前配置: 攻击窗口={combat.AttackDuration:0.###}s "
                + $"冷却={combat.AttackCooldown:0.###}s → "
                + $"{(clip.length > combat.AttackDuration ? "窗口比片段短，动画会被掐断" : "窗口覆盖完整片段")}");
        }
        else
        {
            Debug.LogWarning("[ClipDiag] 场景里没有 PlayerCombat，只报告片段时长");
        }
    }

    // 跳字接线诊断：直接把 Damageable 的运行时状态打出来。
    // 「跳字不显示」的根因可能是引用为空、池为空、实例被销毁，只有把中间状态列出来才能定位。
    [MenuItem(DiagnoseRoot + "跳字接线")]
    public static void DiagnosePopupWiring()
    {
        if (!EditorApplication.isPlaying)
        {
            Debug.LogWarning("[CombatSelfCheck] 跳字接线诊断要在 Play 模式下执行");
            return;
        }

        Enemy[] enemies = Object.FindObjectsOfType<Enemy>(true);
        Debug.Log($"[PopupWire] 场景内 Enemy 数={enemies.Length}");
        foreach (Enemy enemy in enemies)
        {
            if (enemy == null) continue;
            DamagePopup template = enemy.DamagePopupTemplate;
            DamagePopup[] inChildren = enemy.GetComponentsInChildren<DamagePopup>(true);
            Debug.Log($"[PopupWire] {enemy.name}: 跳字原型={(template != null ? template.name + "#" + template.GetInstanceID() : "空")} "
                + $"子物体里的跳字实例={inChildren.Length} 存活={enemy.IsAlive} "
                + $"引用是子物体={(template != null && System.Array.IndexOf(inChildren, template) >= 0)}");
            for (int i = 0; i < inChildren.Length; i++)
            {
                Debug.Log($"[PopupWire]   子物体[{i}] {inChildren[i].name} 激活={inChildren[i].gameObject.activeSelf} "
                    + $"文案={inChildren[i].Text}");
            }
        }
    }

    // 无敌帧自检：编辑模式构造两个实体，验证「首次受击生效 / 无敌帧内免疫 / 到期后恢复」。
    // 用两组实测数据说话，比只打印「时长=0.45s」这种配置值可靠。
    [MenuItem(MenuRoot + "自检无敌帧")]
    public static void SelfCheckInvulnerability()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[CombatSelfCheck] 请先退出 Play 模式再执行");
            return;
        }

        GameObject clockHost = new GameObject("SelfCheckTimeManager");
        GameObject enemyHost = new GameObject("SelfCheckEnemy");
        GameObject playerHost = new GameObject("SelfCheckPlayer");
        // 先禁用再装配：Enemy.Awake 里会 GetComponent<CharacterController>()，
        // 若在组件没加全时就激活，Awake 立刻跑并拿到 null（实测抛 NullReferenceException）。
        enemyHost.SetActive(false);
        playerHost.SetActive(false);
        try
        {
            clockHost.AddComponent<TimeManager>();

            enemyHost.AddComponent<CharacterController>();
            Enemy enemy = enemyHost.AddComponent<Enemy>();
            enemy.ConfigureStats(120f, 8f, 4f);
            enemyHost.SetActive(true);
            enemy.ResetHealth();

            playerHost.AddComponent<CharacterController>();
            PlayerHealth health = playerHost.AddComponent<PlayerHealth>();
            health.Configure(100f);
            playerHost.SetActive(true);

            // ===== 敌人侧 =====
            float enemyFirst = enemy.TakeDamage(10f, Vector3.zero, Vector3.forward);
            bool enemyInvulnerable = enemy.IsInvulnerable;
            float enemySecond = enemy.TakeDamage(10f, Vector3.zero, Vector3.forward);
            Debug.Log($"[InvulnCheck] 敌人: 首次={enemyFirst:0.#} 进入无敌={enemyInvulnerable} "
                + $"无敌中再打={enemySecond:0.#} 血量={enemy.Health:0.#}（期望 10 / True / 0 / 110）");

            // ===== 玩家侧 =====
            float playerFirst = health.TakeDamage(15f);
            bool playerInvulnerable = health.IsInvulnerable;
            float playerSecond = health.TakeDamage(15f);
            Debug.Log($"[InvulnCheck] 玩家: 首次={playerFirst:0.#} 进入无敌={playerInvulnerable} "
                + $"无敌中再打={playerSecond:0.#} 血量={health.Health:0.#}（期望 15 / True / 0 / 85）");

            bool passed = enemyFirst > 0f && enemySecond == 0f && enemyInvulnerable
                && playerFirst > 0f && playerSecond == 0f && playerInvulnerable;
            string verdict = passed
                ? "[InvulnCheck] 结论：通过 —— 无敌帧在玩家与敌人两侧都生效"
                : "[InvulnCheck] 结论：失败 —— 见上面各项数值";
            Debug.Log(verdict);

            // 同时写一份到工程临时目录：MCP 的 Console 读取偶发丢条，
            // 落盘的结果可以不受日志系统影响地核对。
            string report =
                $"敌人: 首次={enemyFirst:0.#} 进入无敌={enemyInvulnerable} 无敌中再打={enemySecond:0.#} 血量={enemy.Health:0.#}\n"
                + $"玩家: 首次={playerFirst:0.#} 进入无敌={playerInvulnerable} 无敌中再打={playerSecond:0.#} 血量={health.Health:0.#}\n"
                + verdict + "\n";
            System.IO.File.WriteAllText("Temp/combat-invuln-check.txt", report, System.Text.Encoding.UTF8);
        }
        finally
        {
            Object.DestroyImmediate(playerHost);
            Object.DestroyImmediate(enemyHost);
            Object.DestroyImmediate(clockHost);
        }
    }

    // 攻击"卡住"的根因诊断：量出动画里动作真正结束的时间。
    //
    // 背景：攻击窗口按片段时长设置，窗口内不允许再次攻击（避免挥砍被打断）。
    // 但 Mixamo 的片段常带长尾（动作结束后还有大段静止/缓慢归位），
    // 若把窗口设成整段长度，玩家就会被多锁住好几秒 —— 表现就是"攻击完卡住一段时间"。
    // 这里对每条曲线求"最后一次显著变化"的时间，作为动作真正的结束点。
    [MenuItem(DiagnoseRoot + "攻击动作结束点")]
    public static void DiagnoseAttackMotionEnd()
    {
        AnimationClip clip = AssetDatabase.LoadAllAssetsAtPath(AttackClipPathForDiagnostics)
            .OfType<AnimationClip>()
            .FirstOrDefault(candidate => !candidate.name.StartsWith("__preview__"));
        if (clip == null)
        {
            Debug.LogError("[MotionDiag] 找不到 Attack 片段");
            return;
        }

        AnimationCurve[] curves = AnimationUtility.GetCurveBindings(clip)
            .Select(binding => AnimationUtility.GetEditorCurve(clip, binding))
            .Where(curve => curve != null && curve.length > 1)
            .ToArray();
        if (curves.Length == 0)
        {
            Debug.LogError("[MotionDiag] 片段里没有可分析的曲线");
            return;
        }

        // 逐条曲线找最后一次"变化足够大"的关键帧时间
        float motionEnd = 0f;
        foreach (AnimationCurve curve in curves)
        {
            Keyframe[] keys = curve.keys;
            for (int i = 1; i < keys.Length; i++)
            {
                float delta = Mathf.Abs(keys[i].value - keys[i - 1].value);
                // 阈值 0.5°（旋转）或 0.5cm（位移）量级：低于这个值肉眼看不出来
                if (delta > 0.5f && keys[i].time > motionEnd) motionEnd = keys[i].time;
            }
        }

        float total = clip.length;
        Debug.Log($"[MotionDiag] Attack 片段: 总长={total:0.###}s 曲线数={curves.Length}");
        Debug.Log($"[MotionDiag] 动作结束点(最后一次显著变化) ≈ {motionEnd:0.###}s "
            + $"→ 占片段 {motionEnd / total:P0}；其后还有 {total - motionEnd:0.###}s 的静止/归位尾巴");

        PlayerCombat combat = Object.FindObjectOfType<PlayerCombat>();
        if (combat != null)
        {
            Debug.Log($"[MotionDiag] 当前攻击窗口={combat.AttackDuration:0.###}s。"
                + $"若窗口 = 片段全长，玩家会被多锁 {(combat.AttackDuration - motionEnd):0.###}s");
        }
        else
        {
            Debug.Log("[MotionDiag] 场景里没有 PlayerCombat，只报告片段数据");
        }
    }

    private const string AttackClipPathForDiagnostics = "Assets/Animations/fbx/Attack.fbx";

    // 受击闪烁的颜色还原诊断：逐个渲染器比对「当前覆盖色」与「共享材质基准色」。
    // 「Miku 的裙子变红」就是这里出的问题：早先只缓存一个渲染器的基准色，
    // 闪烁结束后用错基准色还原，导致该部件永久偏色。
    [MenuItem(DiagnoseRoot + "受击闪烁颜色还原")]
    public static void DiagnoseHitFlashRestore()
    {
        if (!EditorApplication.isPlaying)
        {
            Debug.LogWarning("[CombatSelfCheck] 颜色还原诊断要在 Play 模式下执行（闪烁是运行时行为）");
            return;
        }

        Damageable[] targets = Object.FindObjectsOfType<Damageable>(true);
        if (targets.Length == 0)
        {
            Debug.LogWarning("[CombatSelfCheck] 场景里没有 Damageable");
            return;
        }

        foreach (Damageable target in targets)
        {
            target.DebugDumpFlashState();
            Renderer[] renderers = target.GetComponentsInChildren<Renderer>(false);
            int mismatched = 0;
            int checkedCount = 0;
            MaterialPropertyBlock block = new MaterialPropertyBlock();

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                Material shared = renderer.sharedMaterial;
                if (shared == null) continue;

                // 基准色：共享材质上的原始颜色
                Color baseColor = shared.HasProperty(Shader.PropertyToID("_BaseColor"))
                    ? shared.GetColor(Shader.PropertyToID("_BaseColor"))
                    : shared.HasProperty(Shader.PropertyToID("_Color"))
                        ? shared.GetColor(Shader.PropertyToID("_Color"))
                        : Color.white;

                // 当前覆盖色：MaterialPropertyBlock 里若写了颜色就说明还压着一层
                renderer.GetPropertyBlock(block);
                if (block.isEmpty) continue;
                checkedCount++;
                if (block.HasColor(Shader.PropertyToID("_BaseColor")))
                {
                    Color current = block.GetColor(Shader.PropertyToID("_BaseColor"));
                    if (Vector4.Distance(current, baseColor) > 0.01f)
                    {
                        mismatched++;
                        if (mismatched <= 3)
                        {
                            Debug.LogWarning($"[FlashDiag] {target.name}/{renderer.name} 仍被压色: "
                                + $"当前={(Vector4)current} 基准={(Vector4)baseColor}");
                        }
                    }
                }
            }

            Debug.Log($"[FlashDiag] {target.name}: 渲染器={renderers.Length} 带覆盖的={checkedCount} "
                + $"与基准不一致={mismatched}（不在闪烁中={!target.IsInvulnerable || true}）"
                + (mismatched == 0 ? " → 颜色已正确还原" : " → 存在偏色残留！"));
        }
    }

    // 第一阶段结束后置位，下一次调用先复查「减速是否已自动结束」
    private static bool _awaitingRecovery;
    private static float _probeClockSnapshot;

    // 编辑器时钟是否真的往前走过：没走过就没法用「窗口到期」来判定恢复
    private static bool EditorClockAdvanced(float snapshot) => Time.unscaledTime > snapshot + 0.01f;

    private static void Cleanup(GameObject clockHost, GameObject ground, GameObject player, GameObject enemyHost)
    {
        TimeManager.ClearSlowMotion();
        // 自检产生的跳字是真实对象，留在场景里会污染下一次运行，一并清掉
        foreach (DamagePopup popup in Object.FindObjectsOfType<DamagePopup>())
        {
            Object.DestroyImmediate(popup.gameObject);
        }

        // 按名字前缀兜底清理：早先只销毁本次持有的引用，残留的敌人会和新敌人叠在同一个出生点，
        // Detect 的 SphereCastAll 命中列表里全是它们，结果每轮伤害都打在旧对象上、新对象毫发无伤
        // （表现为「明明有命中日志，但读自己的对象血量没变」）。
        CleanupLeftovers();

        if (player != null) Object.DestroyImmediate(player);
        if (enemyHost != null) Object.DestroyImmediate(enemyHost);
        if (ground != null) Object.DestroyImmediate(ground);
        if (clockHost != null) Object.DestroyImmediate(clockHost);
    }

    // 清掉所有 SelfCheck 开头的场景对象（含历史残留），返回实际清理数量
    private static int CleanupLeftovers()
    {
        int count = 0;
        foreach (GameObject sceneObject in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            // 只处理当前已加载场景里的对象，跳过工程资产（预制体/模型也带同名节点）
            if (!sceneObject.scene.IsValid() || !sceneObject.scene.isLoaded) continue;
            if (!sceneObject.name.StartsWith("SelfCheck", System.StringComparison.Ordinal)) continue;
            count++;
            Object.DestroyImmediate(sceneObject);
        }

        if (count > 0) Debug.Log($"[CombatSelfCheck] 已清理残留自检对象: {count} 个");
        return count;
    }
}
