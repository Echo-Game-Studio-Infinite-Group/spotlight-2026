using System;
using UnityEngine;
using UnityEngine.InputSystem;

[DefaultExecutionOrder(-50)]
[DisallowMultipleComponent]
public sealed class PlayerInputReader : MonoBehaviour, IPlayerInput
{
    [SerializeField] private InputActionAsset _actions;
    private InputActionAsset _runtime;
    private InputActionMap _gameplay;
    private InputAction _move, _look, _sprint, _slide, _skill;
    private bool _jumpPressed, _slidePressed;
    private float _jumpTime;
    private Vector2 _moveValue;
    private bool _sprintHeld, _slideHeld;
    private bool _gameplayEnabled = true;
    private bool _focused = true;
    public event Action ToggleHUD;
    public event Action Cleared;
    public event Action<bool> GameplayChanged;
    public InputBuffer Battle { get; private set; } = new InputBuffer();

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
        // 每个角色持有独立实例，测试或暂停不能停掉另一个角色的 Map。
        _runtime = Instantiate(_actions);
        _gameplay = _runtime.FindActionMap("Gameplay", true);
        _move = _gameplay.FindAction("Move", true);
        _look = _gameplay.FindAction("Look", true);
        _sprint = _gameplay.FindAction("Sprint", true);
        _slide = _gameplay.FindAction("Slide", true);
        _skill = _gameplay.FindAction("Skill", true);
        _move.performed += OnMove;
        _move.canceled += OnMove;
        _sprint.performed += OnSprint;
        _sprint.canceled += OnSprint;
        _gameplay.FindAction("Jump", true).performed += OnJump;
        _slide.performed += OnSlide;
        _slide.canceled += OnSlideCanceled;
        _gameplay.FindAction("Attack", true).performed += OnAttack;
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
    private void OnJump(InputAction.CallbackContext context)
    {
        _jumpPressed = true;
        _jumpTime = TimeManager.UnscaledTime;
        Battle.Push(InputBuffer.Action.Jump, _jumpTime);
    }
    private void OnSlide(InputAction.CallbackContext context) { _slidePressed = true; _slideHeld = true; }
    private void OnSlideCanceled(InputAction.CallbackContext context) => _slideHeld = false;
    private void Update()
    {
        if (_runtime == null || !GameplayEnabled) return;
        _moveValue = _move.ReadValue<Vector2>();
        _sprintHeld = _sprint.IsPressed();
        _slideHeld = _slide.IsPressed();
    }
    private void OnAttack(InputAction.CallbackContext context) => Battle.Push(InputBuffer.Action.Attack, TimeManager.UnscaledTime);
    private void OnSkill(InputAction.CallbackContext context) => Battle.Push(InputBuffer.Action.Skill, TimeManager.UnscaledTime);
    private void OnToggleHUD(InputAction.CallbackContext context) => ToggleHUD?.Invoke();
    private void OnToggleCursor(InputAction.CallbackContext context) => SetGameplayEnabled(!_gameplayEnabled);

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

    public InputBuffer.Intent ConsumeBattleIntent()
    {
        return Battle.ConsumeCombo(TimeManager.UnscaledTime, _skill != null && _skill.IsPressed(),
            _move != null ? _move.ReadValue<Vector2>() : Vector2.zero);
    }

    // 普通攻击的消费口：与 ConsumeBattleIntent 共用同一个缓冲，
    // 但不会把「技能键 + 攻击」的组合意图提前解析掉，两条路径互不吞事件。
    public bool ConsumeAttack(float time) => Battle.ConsumeAttack(time);

    // 招架（Skill 键）消费口：PlayerParry 轮询，与攻击消费同一缓冲不同动作位，互不吞事件
    public bool ConsumeSkill(float time) => Battle.Consume(InputBuffer.Action.Skill, time);
    public void Clear()
    {
        _jumpPressed = _slidePressed = _sprintHeld = _slideHeld = false;
        _moveValue = Vector2.zero;
        Battle.Clear();
        Cleared?.Invoke();
    }
}
