using System;
using UnityEngine;
using UnityEngine.InputSystem;

// 输入统一后端（Input System）：移动帧（IPlayerInput）与战斗输入的唯一设备侧——
// 按下沿推入 InputSampler.Buffer（InputBuffer 仲裁），按住态快照每 Update 写入 InputSampler；
// 物理键 → 逻辑语义的绑定集中在 PlayerControls.inputactions，代码不出现 KeyCode
[DefaultExecutionOrder(-50)]
[DisallowMultipleComponent]
public sealed class PlayerInputReader : MonoBehaviour, IPlayerInput
{
    [SerializeField] private InputActionAsset _actions;
    private InputActionAsset _runtime;
    private InputActionMap _gameplay;
    private InputAction _move, _look, _sprint, _slide, _skill, _attack, _jump, _dash;
    private InputSampler _combatInput; // 战斗输入持有器；战斗组件未装配时为 null（推送静默跳过）
    private bool _jumpPressed, _slidePressed;
    private float _jumpTime;
    private Vector2 _moveValue;
    private bool _sprintHeld, _slideHeld;
    private bool _gameplayEnabled = true;
    private bool _focused = true;
    public event Action ToggleHUD;
    public event Action Cleared;
    public event Action<bool> GameplayChanged;

    public bool GameplayEnabled => _gameplayEnabled && _focused;
    public Vector2 LookDelta => _look != null && GameplayEnabled ? _look.ReadValue<Vector2>() : Vector2.zero;
    public void Configure(InputActionAsset actions) => _actions = actions;

    private void Awake()
    {
        if (_actions == null)
        {
            Debug.LogError("[PlayerInputReader] 缺少 PlayerControls.inputactions", this);
            enabled = false;
            return;
        }
        _combatInput = GetComponent<InputSampler>();
        // 每个角色持有独立实例，测试或暂停不能停掉另一个角色的 Map。
        _runtime = Instantiate(_actions);
        _gameplay = _runtime.FindActionMap("Gameplay", true);
        _move = _gameplay.FindAction("Move", true);
        _look = _gameplay.FindAction("Look", true);
        _sprint = _gameplay.FindAction("Sprint", true);
        _slide = _gameplay.FindAction("Slide", true);
        _skill = _gameplay.FindAction("Skill", true);
        _attack = _gameplay.FindAction("Attack", true);
        _jump = _gameplay.FindAction("Jump", true);
        _dash = _gameplay.FindAction("Dash", false); // Q 修饰键：资产未配则视为未按住，不硬失败
        _move.performed += OnMove;
        _move.canceled += OnMove;
        _sprint.performed += OnSprint;
        _sprint.canceled += OnSprint;
        _sprint.performed += OnSprintPressed;
        _jump.performed += OnJump;
        _slide.performed += OnSlide;
        _slide.canceled += OnSlideCanceled;
        _attack.performed += OnAttack;
        _skill.performed += OnSkill;
        _runtime.FindAction("Debug/ToggleHUD", true).performed += OnToggleHUD;
        _runtime.FindAction("Debug/ToggleCursor", true).performed += OnToggleCursor;
    }

    private void OnEnable()
    {
        if (_runtime == null) return;
        _runtime.FindActionMap("Debug", true).Enable();
        ApplyGameplay();
    }
    private void OnDisable() { _runtime?.Disable(); Clear(); }
    private void OnDestroy() { if (_runtime != null) Destroy(_runtime); }
    private void OnApplicationFocus(bool focus) { _focused = focus; ApplyGameplay(); }
    private void OnMove(InputAction.CallbackContext context) => _moveValue = context.ReadValue<Vector2>();
    private void OnSprint(InputAction.CallbackContext context) => _sprintHeld = context.ReadValueAsButton();
    private void OnSprintPressed(InputAction.CallbackContext context) => _combatInput?.Buffer.Push(LogicalButton.DodgeShift);
    private void OnJump(InputAction.CallbackContext context)
    {
        _jumpPressed = true;
        _jumpTime = TimeManager.UnscaledTime;
        _combatInput?.Buffer.Push(LogicalButton.Jump);
    }
    private void OnSlide(InputAction.CallbackContext context) { _slidePressed = true; _slideHeld = true; }
    private void OnSlideCanceled(InputAction.CallbackContext context) => _slideHeld = false;
    private void OnAttack(InputAction.CallbackContext context) => _combatInput?.Buffer.Push(LogicalButton.Attack);
    private void OnSkill(InputAction.CallbackContext context) => _combatInput?.Buffer.Push(LogicalButton.Skill);
    private void OnToggleHUD(InputAction.CallbackContext context) => ToggleHUD?.Invoke();
    private void OnToggleCursor(InputAction.CallbackContext context) => SetGameplayEnabled(!_gameplayEnabled);

    private void Update()
    {
        if (_runtime == null) return;
        if (!GameplayEnabled)
        {
            // 禁用期间按住态清零：恢复时不得残留旧修饰（按下沿由缓冲窗口自然过期）
            _combatInput?.InjectSnapshot(InputSnapshot.Empty);
            return;
        }
        _moveValue = _move.ReadValue<Vector2>();
        _sprintHeld = _sprint.IsPressed();
        _slideHeld = _slide.IsPressed();
        _combatInput?.InjectSnapshot(new InputSnapshot
        {
            Horizontal = _moveValue.x,
            Vertical = _moveValue.y,
            ForwardHeld = _moveValue.y > 0f,
            BackHeld = _moveValue.y < 0f,
            LeftHeld = _moveValue.x < 0f,
            RightHeld = _moveValue.x > 0f,
            SprintHeld = _sprintHeld,
            SlideHeld = _slideHeld,
            DashHeld = _dash != null && _dash.IsPressed(),
            AttackHeld = _attack.IsPressed(),
            SkillHeld = _skill.IsPressed(),
            JumpHeld = _jump.IsPressed(),
        });
    }

    public void SetGameplayEnabled(bool enabled) { _gameplayEnabled = enabled; ApplyGameplay(); }
    private void ApplyGameplay()
    {
        Clear();
        if (_gameplay == null) return;
        if (GameplayEnabled) _gameplay.Enable(); else _gameplay.Disable();
        GameplayChanged?.Invoke(GameplayEnabled);
    }

    public PlayerInputFrame ReadFrame()
    {
        if (_runtime == null || !GameplayEnabled) return default;
        PlayerInputFrame frame = new PlayerInputFrame
        {
            Move = _moveValue, SprintHeld = _sprintHeld, SlideHeld = _slideHeld,
            SlidePressed = _slidePressed, JumpPressed = _jumpPressed, JumpTime = _jumpTime
        };
        _jumpPressed = _slidePressed = false;
        return frame;
    }

    public void Clear()
    {
        _jumpPressed = _slidePressed = _sprintHeld = _slideHeld = false;
        _moveValue = Vector2.zero;
        Cleared?.Invoke();
    }
}
