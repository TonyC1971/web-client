using ClassicUO.Configuration;

namespace ClassicUO.Game.Managers;

public static class GlobalActionCooldown
{
    private static long _nextActionTime = 0;
    private static long _cooldownDuration => ProfileManager.CurrentProfile?.MoveMultiObjectDelay ?? 1000;
    public static long CooldownDuration => _cooldownDuration;

    public static bool IsOnCooldown => Time.Ticks < _nextActionTime;
    public static void BeginCooldown() => _nextActionTime = Time.Ticks + _cooldownDuration;
}
