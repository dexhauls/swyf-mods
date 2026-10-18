using System.Collections;
using System.Reflection;
using HarmonyLib;
using Metater;
using Mirror;
using UnityEngine;

namespace Emotes
{
    /// <summary>Plays wheel choices through the game's own networked commands.</summary>
    internal static class LocalPlayer
    {
        private static readonly MethodInfo TryPlayClip = AccessTools.Method(typeof(PlayerEmoterNetworking), "TryPlayClip");
        private static readonly MethodInfo CmdPlayEmote = AccessTools.Method(typeof(PlayerEmoterNetworking), "CmdPlayEmote");
        private static readonly MethodInfo IsPhysicallyBlocked = AccessTools.Method(typeof(PlayerEmoterNetworking), "IsPhysicallyBlocked");
        private static readonly MethodInfo IsLocalInputBlocked = AccessTools.Method(typeof(PlayerEmoterNetworking), "IsLocalInputBlocked");
        private static readonly MethodInfo RpcPlayLocal =AccessTools.Method(typeof(PlayerEmoterNetworking), "UserCode_RpcPlayEmote__String");
        private static readonly MethodInfo CmdScratch = AccessTools.Method(typeof(MetaPlayer), "CmdManualButtScratch");
        private static readonly MethodInfo CmdFart = AccessTools.Method(typeof(MetaPlayer), "CmdPlayManualFart");
        private static readonly MethodInfo CmdWobble = AccessTools.Method(typeof(MetaPlayer), "CmdManualHeadWobble");

        private static GameObject Me => NetworkClient.localPlayer != null ? NetworkClient.localPlayer.gameObject : null;

        public static bool Exists => Me != null;

        public static void Play(EmoteEntry e, MonoBehaviour host)
        {
            var me = Me;
            if (me == null) return;
            switch (e.Kind)
            {
                case EmoteKind.Catalog:
                case EmoteKind.Extra:
                {
                    var emoter = me.GetComponent<PlayerEmoterNetworking>();
                    var clip = e.Kind == EmoteKind.Catalog ? EmoteCatalog.FindCatalog(e.Clip) : EmoteCatalog.FindExtra(e.Clip);
                    if (emoter == null || clip == null) return;
                    TryPlayClip.Invoke(emoter, new object[] { clip });
                    host.StartCoroutine(Report(emoter, e));
                    // A host without the mod ignores extra emotes; then at least you see it yourself.
                    if (e.Kind == EmoteKind.Extra && !NetworkServer.active) host.StartCoroutine(LocalFallback(emoter, clip.name));
                    break;
                }
                case EmoteKind.Dance:
                {
                    // The game's TryPlayClip sends the clip's own name; a dance travels under its id instead.
                    var emoter = me.GetComponent<PlayerEmoterNetworking>();
                    if (emoter == null || (bool)IsPhysicallyBlocked.Invoke(emoter, null) || (bool)IsLocalInputBlocked.Invoke(emoter, null)) return;
                    CmdPlayEmote.Invoke(emoter, new object[] { e.Clip });
                    host.StartCoroutine(Report(emoter, e));
                    if (!NetworkServer.active) host.StartCoroutine(LocalFallback(emoter, e.Clip));
                    break;
                }
                case EmoteKind.ButtScratch:
                    Plugin.Log.LogInfo($"Emote '{e.Label}' ({e.Kind})");
                    Invoke(me.GetComponent<MetaPlayer>(), CmdScratch);
                    break;
                case EmoteKind.Fart:
                    Invoke(me.GetComponent<MetaPlayer>(), CmdFart);
                    break;
                case EmoteKind.HeadWobble:
                    Invoke(me.GetComponent<MetaPlayer>(), CmdWobble);
                    break;
            }
        }

        private static IEnumerator Report(PlayerEmoterNetworking emoter, EmoteEntry e)
        {
            yield return new WaitForSecondsRealtime(0.3f);
            Plugin.Log.LogInfo($"Emote '{e.Label}' ({e.Clip}, {e.Kind}): " + (emoter != null && emoter.IsEmoting ? "playing" : "not playing (moving, blocked or rejected)"));
        }

        private static IEnumerator LocalFallback(PlayerEmoterNetworking emoter, string clip)
        {
            yield return new WaitForSecondsRealtime(0.4f);
            if (emoter != null && !emoter.IsEmoting)
            {
                Plugin.Log.LogInfo($"The host did not play '{clip}' (no Emoty mod there?), playing it just for you.");
                RpcPlayLocal.Invoke(emoter, new object[] { clip });
            }
        }

        private static void Invoke(Object target, MethodInfo method)
        {
            if (target != null && method != null) method.Invoke(target, null);
        }
    }
}
