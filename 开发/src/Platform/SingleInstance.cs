using System;
using System.Threading;

namespace DeviceBattery
{
    internal static class SingleInstance
    {
        private const string EventName = "VoltPick.ShowPanel";

        private static readonly object Gate = new object();
        private static EventWaitHandle _handle;
        private static Thread _listener;
        private static volatile bool _running;
        private static bool _listening;

        public static Action ShowRequested;

        public static void Listen()
        {
            lock (Gate)
            {
                if (_listening)
                {
                    return;
                }
                _listening = true;
                _running = true;
            }

            try
            {
                _handle = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            }
            catch (Exception ex)
            {
                Log.Warn("单实例唤醒事件不可用：" + ex.Message);
                lock (Gate)
                {
                    _listening = false;
                }
                _running = false;
                return;
            }

            _listener = new Thread(ListenLoop);
            _listener.IsBackground = true;
            _listener.Name = "ShowPanelListener";
            _listener.Start();
        }

        private static void ListenLoop()
        {
            try
            {
                while (_running)
                {
                    EventWaitHandle handle = _handle;
                    if (handle == null)
                    {
                        return;
                    }

                    if (!handle.WaitOne(500, false))
                    {
                        continue;
                    }

                    try
                    {
                        Action handler = ShowRequested;
                        if (handler != null)
                        {
                            handler();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("单实例显示面板异常：" + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("单实例监听线程异常：" + ex.Message);
            }
        }

        public static bool SignalExisting()
        {
            // 后启动的进程可能比先启动的进程更早跑到这里：先启动那边刚抢到互斥体、
            // 还没执行到 Listen() 创建事件对象，此时 TryOpenExisting 会失败。
            // 不重试的话第二次双击就是静默消失（面板不弹），所以给几次机会。
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (attempt > 0)
                {
                    Thread.Sleep(150);
                }
                try
                {
                    EventWaitHandle handle;
                    if (EventWaitHandle.TryOpenExisting(EventName, out handle))
                    {
                        using (handle)
                        {
                            bool ok = handle.Set();
                            // 留一点时间让对方线程把面板显示出来再退出。
                            Thread.Sleep(120);
                            return ok;
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            Log.Warn("未能唤醒已有实例：事件对象 " + EventName + " 一直打不开");
            return false;
        }

        public static void Stop()
        {
            _running = false;

            Thread listener;
            lock (Gate)
            {
                listener = _listener;
                _listener = null;
            }

            bool stopped = true;
            try
            {
                if (listener != null)
                {
                    stopped = listener.Join(1500);
                }
            }
            catch (Exception)
            {
                stopped = false;
            }

            lock (Gate)
            {
                _listening = false;
            }

            if (!stopped)
            {
                // 监听线程还卡在 WaitOne 里，这时候关句柄会让它抛 ObjectDisposedException。
                // 进程马上就退，句柄交给系统回收，不再手动关。
                Log.Warn("单实例监听线程未在 1.5 秒内退出，跳过关闭事件句柄");
                return;
            }

            EventWaitHandle handle = _handle;
            _handle = null;
            try
            {
                if (handle != null)
                {
                    handle.Close();
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
