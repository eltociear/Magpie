﻿/*
 * ---------------------
 *   非常强大，非常脆弱
 * ---------------------
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;


namespace Magpie.CursorHook {
    /// <summary>
    /// 注入时 EasyHook 会寻找 <see cref="EasyHook.IEntryPoint"/> 的实现。
    /// 注入后此类将成为入口
    /// </summary>
    public class InjectionEntryPoint : EasyHook.IEntryPoint {
#if DEBUG
        // 用于向 Magpie 里的 IPC server 发送消息
        private readonly ServerInterface _server;

        private readonly Queue<string> _messageQueue = new Queue<string>();
#endif

        private readonly IntPtr _hwndHost;
        private readonly IntPtr _hwndSrc;

        private readonly (int x, int y) _cursorSize = NativeMethods.GetCursorSize();
        
        // 用于保存窗口类的 HCURSOR，以在卸载钩子时还原
        private readonly Dictionary<IntPtr, IntPtr> _hwndTohCursor = new Dictionary<IntPtr, IntPtr>();

        // 原光标到透明光标的映射
        private readonly Dictionary<IntPtr, IntPtr> _hCursorToTptCursor = new Dictionary<IntPtr, IntPtr>();


        // EasyHook 需要此方法作为入口
        public InjectionEntryPoint(
            EasyHook.RemoteHooking.IContext _,
#if DEBUG
            string channelName,
#endif
            IntPtr hwndHost, IntPtr hwndSrc
         ) {
            _hwndHost = hwndHost;
            _hwndSrc = hwndSrc;
#if DEBUG
            // DEBUG 时连接 IPC server
            _server = EasyHook.RemoteHooking.IpcConnectClient<ServerInterface>(channelName);

            // 测试连接性，如果失败会抛出异常静默的失败因此 Run 方法不会执行
            _server.Ping();
#endif
        }

        // 注入逻辑的入口
        public void Run(
            EasyHook.RemoteHooking.IContext _,
#if DEBUG
            string _1,
#endif
            IntPtr _2, IntPtr _3
        ) {
            // 安装钩子

            // 截获 SetCursor
            var setCursorHook = EasyHook.LocalHook.Create(
                EasyHook.LocalHook.GetProcAddress("user32.dll", "SetCursor"),
                new SetCursor_Delegate(SetCursor_Hook),
                this
            );

            // Hook 当前线程外的所有线程
            setCursorHook.ThreadACL.SetExclusiveACL(new int[] { 0 });

            ReportToServer("SetCursor钩子安装成功");

            // 不替换这些系统光标，因为已被全局替换
            var arrowCursor = NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_ARROW);
            var handCursor = NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_HAND);
            var appStartingCursor = NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_APPSTARTING);
            _hCursorToTptCursor[arrowCursor] = arrowCursor;
            _hCursorToTptCursor[handCursor] = handCursor;
            _hCursorToTptCursor[appStartingCursor] = appStartingCursor;

            // 将窗口类中的 HCURSOR 替换为透明光标
            void replaceHCursor(IntPtr hWnd) {
                if (_hwndTohCursor.ContainsKey(hWnd)) {
                    return;
                }

                // Get(Set)ClassLong 不能使用 Ptr 版本
                IntPtr hCursor = (IntPtr)NativeMethods.GetClassLong(hWnd, NativeMethods.GCLP_HCURSOR);
                if (hCursor == IntPtr.Zero) {
                    return;
                }

                _hwndTohCursor[hWnd] = hCursor;

                IntPtr hTptCursor = IntPtr.Zero;
                if (_hCursorToTptCursor.ContainsKey(hCursor)) {
                    // 透明的系统光标或之前已替换过的
                    hTptCursor = _hCursorToTptCursor[hCursor];
                } else {
                    hTptCursor = GetReplacedCursor(hCursor);
                }

                // 替换窗口类的 HCURSOR
                NativeMethods.SetClassLong(hWnd, NativeMethods.GCLP_HCURSOR, hTptCursor);
            }

            // 替换进程中所有顶级窗口的窗口类的 HCURSOR
            /*NativeMethods.EnumChildWindows(IntPtr.Zero, (IntPtr hWnd, int processId) => {
                if (NativeMethods.GetWindowProcessId(hWnd) != processId) {
                    return true;
                }

                replaceHCursor(hWnd);
                NativeMethods.EnumChildWindows(hWnd, (IntPtr hWnd1, int _4) => {
                    replaceHCursor(hWnd1);
                    return true;
                }, 0);
                return true;
            }, NativeMethods.GetWindowProcessId(_hwndSrc));*/

            // 替换源窗口和它的所有子窗口的窗口类的 HCRUSOR
            // 因为通过窗口类的 HCURSOR 设置光标不会通过 SetCursor
            IntPtr hwndTop = NativeMethods.GetTopWindow(_hwndSrc);
            replaceHCursor(hwndTop);
            NativeMethods.EnumChildWindows(hwndTop, (IntPtr hWnd, int _4) => {
                replaceHCursor(hWnd);
                return true;
            }, 0);

            // 向源窗口发送 WM_SETCURSOR，一般可以使其调用 SetCursor
            NativeMethods.PostMessage(
                _hwndSrc,
                NativeMethods.WM_SETCURSOR,
                _hwndSrc,
                (IntPtr)NativeMethods.HTCLIENT
            );
            
            try {
                // Loop until FileMonitor closes (i.e. IPC fails)
                while (true) {
                    Thread.Sleep(200);

                    if(!NativeMethods.IsWindow(_hwndHost)) {
                        // 全屏窗口已关闭，卸载钩子
                        break;
                    }

#if DEBUG
                    string[] queued = _messageQueue.ToArray();
                    _messageQueue.Clear();

                    if (queued.Length > 0) {
                        _server.ReportMessages(queued);
                    } else {
                        _server.Ping();
                    }
#endif
                }
            } catch {
                // 如果服务器关闭 Ping() 和 ReportMessages() 将抛出异常
                // 执行到此处表示 Magpie 已关闭
            }

            // 退出前重置窗口类的光标
            foreach (var item in _hwndTohCursor) {
                NativeMethods.SetClassLong(item.Key, NativeMethods.GCLP_HCURSOR, item.Value);
            }

            // 卸载钩子
            setCursorHook.Dispose();
            EasyHook.LocalHook.Release();
        }


        // 用于创建 SetCursor 委托
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
        delegate IntPtr SetCursor_Delegate(IntPtr hCursor);


        // 取代 SetCursor 的钩子
        IntPtr SetCursor_Hook(IntPtr hCursor) {
            // ReportToServer("setcursor");

            if (!NativeMethods.IsWindow(_hwndHost) || hCursor == IntPtr.Zero) {
                // 全屏窗口关闭后钩子不做任何操作
                return NativeMethods.SetCursor(hCursor);
            }

            if(_hCursorToTptCursor.ContainsKey(hCursor)) {
                return NativeMethods.SetCursor(_hCursorToTptCursor[hCursor]);
            }

            // 未出现过的 hCursor
            return NativeMethods.SetCursor(GetReplacedCursor(hCursor));
        }

        private IntPtr GetReplacedCursor(IntPtr hCursor) {
            IntPtr hTptCursor = CreateTransparentCursor(0, 0);
            if (hTptCursor == IntPtr.Zero) {
                return hCursor;
            }

            _hCursorToTptCursor[hCursor] = hTptCursor;

            // 向全屏窗口发送光标句柄
            NativeMethods.PostMessage(_hwndHost, NativeMethods.MAGPIE_WM_NEWCURSOR, hTptCursor, hCursor);

            return hTptCursor;
        }

        private IntPtr CreateTransparentCursor(int xHotSpot, int yHotSpot) {
            int len = _cursorSize.x * _cursorSize.y;

            // 全 0xff
            byte[] andPlane = new byte[len];
            for (int i = 0; i < len; ++i) {
                andPlane[i] = 0xff;
            }

            // 全 0
            byte[] xorPlane = new byte[len];

            return NativeMethods.CreateCursor(NativeMethods.GetModule(), xHotSpot, yHotSpot,
                _cursorSize.x, _cursorSize.y, andPlane, xorPlane);
        }

        private void ReportToServer(string msg) {
#if DEBUG
            _messageQueue.Enqueue(msg);
#endif
        }
    }
}