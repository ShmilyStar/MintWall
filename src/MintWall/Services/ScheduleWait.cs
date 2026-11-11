using System;
using System.Threading;
using System.Threading.Tasks;

namespace MintWall.Services;

/// <summary>可以提前打断定时等待的信号源（系统唤醒、时间被调整）。</summary>
internal interface IWakeSignal
{
    bool TryArm(out Task signal);
}

/// <summary>
/// 按墙上时间等待 deadline 的纯逻辑：正常只挂一个长延时，事件到来时重新判断；
/// 抽出来便于用假时钟验证“休眠跨过触发点后立即补触发”。
/// </summary>
internal static class ScheduleWait
{
    public static async Task<bool> UntilAsync(DateTime deadline, IWakeSignal wake, Func<DateTime> now, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (wake.TryArm(out Task signal))
                {
                    continue;
                }

                TimeSpan wait = deadline - now();
                if (wait <= TimeSpan.Zero)
                {
                    return true;
                }

                Task delay = Task.Delay(wait, ct);
                await Task.WhenAny(delay, signal);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return false;
    }
}
