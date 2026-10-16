using Microsoft.Win32;
using System;

namespace UnlockServer
{
    /// <summary>
    /// 会话切换监听类
    /// </summary>
    public class SessionSwitchClass
    {
        /// <summary>
        /// 解屏后执行的委托
        /// </summary>
        public Action SessionUnlockAction { get; set; }

        /// <summary>
        /// 锁屏后执行的委托
        /// </summary>
        public Action SessionLockAction { get; set; }

        public bool isUnlockBySoft = false;
        public bool isLockBySoft = true;
        public bool dolocking = false;
        public bool dounlocking = false;
        public bool IsLocked { get; private set; }

        /// <summary>
        /// 上一次由程序主动发起锁屏的时间。用于区分「软件锁屏」与「人工 Win+L」：
        /// 只有紧邻软件锁屏（数秒内）到来的 SessionLock 才算软件锁屏，
        /// 否则一律视为人工锁定，从而让「不干预手动锁定」真正生效。
        /// </summary>
        private DateTime _softLockRequestedAt = DateTime.MinValue;
        private static readonly TimeSpan SoftLockWindow = TimeSpan.FromSeconds(5);

        /// <summary>由程序在调用 LockWorkStation 之前置位，标记「这次锁屏是软件发起的」。</summary>
        public void MarkSoftLockRequested()
        {
            _softLockRequestedAt = DateTime.Now;
            dolocking = true;
        }

        public SessionSwitchClass()
        {
            IsLocked = WanClient.IsSessionLocked();
            SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
        }

        public void Close()
        {
            SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
        }

        /// <summary>
        /// 当前登录的用户变化（登录、注销和解锁屏）
        /// </summary>
        public void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLogon:
                    BeginSessionUnlock();
                    break;
                case SessionSwitchReason.SessionUnlock:
                    BeginSessionUnlock();
                    break;
                case SessionSwitchReason.SessionLock:
                    BeginSessionLock();
                    break;
                case SessionSwitchReason.SessionLogoff:
                    break;
            }
        }

        /// <summary>
        /// 解屏、登录后执行
        /// </summary>
        private void BeginSessionUnlock()
        {
            if (dounlocking)
            {
                isUnlockBySoft = true;
            }
            else
            {
                isUnlockBySoft = false;
            }
            dounlocking = false;
            IsLocked = false;

            SessionUnlockAction?.Invoke();
        }

        /// <summary>
        /// 锁屏后执行
        /// </summary>
        private void BeginSessionLock()
        {
            // 判定本次锁屏是否由程序发起（软件锁屏）：
            //   1) 标志位 dolocking 为真（程序正准备/正在锁屏），或
            //   2) 距离程序主动请求锁屏很近（5 秒内）—— 用于 dolocking 被提前清掉的兜底。
            // 除此之外一律视为人工锁定（含 Win+L、开始菜单锁屏、超时锁屏），
            // 这样「不干预手动锁定」才不会被误判绕过。
            bool softRequested = dolocking
                || (_softLockRequestedAt != DateTime.MinValue
                    && (DateTime.Now - _softLockRequestedAt) <= SoftLockWindow);

            if (softRequested)
            {
                isLockBySoft = true;
            }
            else
            {
                isLockBySoft = false;
            }

            dolocking = false;
            _softLockRequestedAt = DateTime.MinValue;
            IsLocked = true;

            SessionLockAction?.Invoke();
        }
    }
}

