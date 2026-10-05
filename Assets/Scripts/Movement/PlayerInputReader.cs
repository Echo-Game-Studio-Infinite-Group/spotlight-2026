using System;
using System.Collections.Generic;
using GameJam.Actions;
using UnityEngine;
using UnityEngine.InputSystem;

[DefaultExecutionOrder(-50)]
[DisallowMultipleComponent]
public sealed class PlayerInputReader : MonoBehaviour, IPlayerInput
{
    [SerializeField] private InputActionAsset _actions;
    private InputActionAsset _runtime;
    private InputActionMap _gameplay;
    private InputAction _move, _look, _sprint, _slide, _skill, _attack;
    private readonly List<ActionInputEdge> _actionEdges = new List<ActionInputEdge>();
    private ActionInputButtons _heldButtons;
    private object _sequenceOwner;
    private long _snapshotTick = long.MinValue;
    private ActionInputSample _actionSnapshot;
    private PlayerInputFrame _motorSnapshot;
    private bool _jumpPressed, _slidePressed;
    private float _jumpTime;
    private Vector2 _moveValue;
    private bool _sprintHeld, _slideHeld;
    private bool _gameplayEnabled = true;
    private bool _focused = true;
    public event Action ToggleHUD;
    public event Action Cleared;
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
        _attack = _gameplay.FindAction("Attack", true);
        _move.performed += OnMove;
        _move.canceled += OnMove;
        _sprint.performed += OnSprint;
        _sprint.canceled += OnSprint;
        _gameplay.FindAction("Jump", true).performed += OnJump;
        _gameplay.FindAction("Jump", true).canceled += context => RecordEdge(ActionInputButtons.Jump, false);
        _slide.performed += OnSlide;
        _slide.canceled += OnSlideCanceled;
        _attack.performed += OnAttack;
        _attack.canceled += context => RecordEdge(ActionInputButtons.Attack, false);
        _skill.performed += OnSkill;
        _skill.canceled += context => RecordEdge(ActionInputButtons.Skill, false);
        _runtime.FindAction("Debug/ToggleHUD", true).performed += OnToggleHUD;
        _runtime.FindAction("Debug/ToggleCursor", true).performed += OnToggleCursor;
    }

    private void OnEnable()
    {
        if (_runtime == null) return;
        _runtime.FindActionMap("Debug", true).Enable();
        ApplyGameplay();
    }
    private void OnDisable() { _runtime?.Disable(); Clear(); ApplyCursor(false); }
    private void OnDestroy() { if (_runtime != null) Destroy(_runtime); }
    private void OnApplicationFocus(bool focus) { _focused = focus; ApplyGameplay(); }
    private void OnMove(InputAction.CallbackContext context)
    {
        _moveValue = context.ReadValue<Vector2>();
        RecordEdge(ActionInputButtons.Forward, _moveValue.y > 0f);
        RecordEdge(ActionInputButtons.Backward, _moveValue.y < 0f);
        RecordEdge(ActionInputButtons.Left, _moveValue.x < 0f);
        RecordEdge(ActionInputButtons.Right, _moveValue.x > 0f);
    }
    private void OnSprint(InputAction.CallbackContext context)
    {
        _sprintHeld = context.ReadValueAsButton();
        RecordEdge(ActionInputButtons.Sprint, _sprintHeld);
    }
    private void OnJump(InputAction.CallbackContext context)
    {
        _jumpPressed = true;
        _jumpTime = TimeManager.UnscaledTime;
        RecordEdge(ActionInputButtons.Jump, true);
        if (_sequenceOwner == null) Battle.Push(InputBuffer.Action.Jump, _jumpTime);
    }
    private void OnSlide(InputAction.CallbackContext context) { _slidePressed = true; _slideHeld = true; RecordEdge(ActionInputButtons.Slide, true); }
    private void OnSlideCanceled(InputAction.CallbackContext context) { _slideHeld = false; RecordEdge(ActionInputButtons.Slide, false); }
    private void Update()
    {
        if (_runtime == null || !GameplayEnabled) return;
        _moveValue = _move.ReadValue<Vector2>();
        _sprintHeld = _sprint.IsPressed();
        _slideHeld = _slide.IsPressed();
    }
    private void OnAttack(InputAction.CallbackContext context)
    {
        RecordEdge(ActionInputButtons.Attack, true);
        if (_sequenceOwner == null) Battle.Push(InputBuffer.Action.Attack, TimeManager.UnscaledTime);
    }
    private void OnSkill(InputAction.CallbackContext context)
    {
        RecordEdge(ActionInputButtons.Skill, true);
        if (_sequenceOwner == null) Battle.Push(InputBuffer.Action.Skill, TimeManager.UnscaledTime);
    }
    private void RecordEdge(ActionInputButtons button, bool pressed)
    {
        bool wasHeld = (_heldButtons & button) != 0;
        if (pressed == wasHeld) return;
        if (pressed) _heldButtons |= button; else _heldButtons &= ~button;
        // 未接入序列时无需积累另一份历史，避免闲置缓冲无限增长。
        if (_sequenceOwner != null)
        {
            ActionInputButtons atEvent = _heldButtons;
            // 同一设备事件内两个键同时变化时，修饰键不能依赖回调先后顺序。
            if (_skill != null)
            {
                bool skillHeld = false;
                foreach (InputControl control in _skill.controls)
                    if (control.device.added && control is UnityEngine.InputSystem.Controls.ButtonControl key && key.isPressed) skillHeld = true;
                if (skillHeld) atEvent |= ActionInputButtons.Skill; else atEvent &= ~ActionInputButtons.Skill;
            }
            _actionEdges.Add(new ActionInputEdge(button, pressed ? ActionInputStep.Edge.Pressed : ActionInputStep.Edge.Released, atEvent, _moveValue));
        }
    }

    public bool TryAcquireSequenceInput(object owner)
    {
        if (owner == null || _sequenceOwner != null && !ReferenceEquals(owner, _sequenceOwner)) return false;
        _sequenceOwner = owner;
        _snapshotTick = long.MinValue;
        _actionEdges.Clear();
        Battle.Clear();
        return true;
    }
    public void ReleaseSequenceInput(object owner)
    {
        if (!ReferenceEquals(owner, _sequenceOwner)) return;
        _sequenceOwner = null;
        _snapshotTick = long.MinValue;
        _actionEdges.Clear();
        Battle.Clear();
    }

    public ActionInputSample CaptureTick(long tick, out PlayerInputFrame motorFrame)
    {
        if (_snapshotTick != tick)
        {
            _motorSnapshot = ReadFrame();
            _actionSnapshot = new ActionInputSample(tick, GameplayEnabled ? _heldButtons : ActionInputButtons.None,
                _motorSnapshot.Move, _actionEdges.Count > 0 ? _actionEdges.ToArray() : null);
            _actionEdges.Clear();
            _snapshotTick = tick;
        }
        motorFrame = _motorSnapshot;
        return _actionSnapshot;
    }
    private void OnToggleHUD(InputAction.CallbackContext context) => ToggleHUD?.Invoke();
    private void OnToggleCursor(InputAction.CallbackContext context) => SetGameplayEnabled(!_gameplayEnabled);

    public void SetGameplayEnabled(bool enabled) { _gameplayEnabled = enabled; ApplyGameplay(); }
    private void ApplyGameplay()
    {
        Clear();
        if (_gameplay == null) return;
        if (GameplayEnabled) _gameplay.Enable(); else _gameplay.Disable();
        ApplyCursor(GameplayEnabled);
    }

    // 光标是输入态的一部分：暂停、失焦、按 Esc 都要交还光标，否则界面点不动。
    // 这里统一裁决，相机与 HUD 不再各自操作 Cursor。
    private void ApplyCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
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
    public void Clear()
    {
        _jumpPressed = _slidePressed = _sprintHeld = _slideHeld = false;
        _moveValue = Vector2.zero;
        Battle.Clear();
        _actionEdges.Clear();
        _heldButtons = ActionInputButtons.None;
        _snapshotTick = long.MinValue;
        Cleared?.Invoke();
    }
}
