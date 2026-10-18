using System;

namespace GameJam.Actions
{
    [Flags]
    public enum ActionInputButtons
    {
        None = 0,
        Attack = 1 << 0,
        Skill = 1 << 1,
        Jump = 1 << 2,
        Sprint = 1 << 3,
        Slide = 1 << 4,
        Forward = 1 << 5,
        Backward = 1 << 6,
        Left = 1 << 7,
        Right = 1 << 8,
        Parry = 1 << 9,
        // 保留远端 Parry 的序列化编号，刹车另占一位，避免两种输入互相触发。
        Brake = 1 << 10
    }
}
