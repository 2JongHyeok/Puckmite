using UnityEngine;

namespace Puckmite.View
{
    /// <summary>
    /// The one audio surface (사용자 사운드 2026-08-10): a hidden, scene-surviving object carrying a
    /// looping BGM source and a one-shot SFX source. Volumes persist in PlayerPrefs — the ESC menu's
    /// sliders write them here. Per-clip gains live at the call sites, because the clips' measured
    /// loudness differs and the callers pass the factor that evens them out.
    /// </summary>
    public static class GameAudio
    {
        private const string SfxKey = "PuckHero.SfxVolume";
        private const string BgmKey = "PuckHero.BgmVolume";
        private const float BgmBaseGain = 0.35f; // the music's RMS dwarfs the SFX clips; sliders sit on top

        private static GameObject _root;
        private static AudioSource _bgm;
        private static AudioSource _sfx;
        private static float _sfxVolume;
        private static float _bgmVolume;

        public static float SfxVolume
        {
            get
            {
                Ensure();
                return _sfxVolume;
            }
            set
            {
                Ensure();
                _sfxVolume = Mathf.Clamp01(value);
                PlayerPrefs.SetFloat(SfxKey, _sfxVolume);
            }
        }

        public static float BgmVolume
        {
            get
            {
                Ensure();
                return _bgmVolume;
            }
            set
            {
                Ensure();
                _bgmVolume = Mathf.Clamp01(value);
                PlayerPrefs.SetFloat(BgmKey, _bgmVolume);
                _bgm.volume = _bgmVolume * BgmBaseGain;
            }
        }

        /// <summary>Starts (or keeps) the looping BGM — scene loads call this every time, and an already
        /// playing identical clip just carries on, so the music never hitches between battle and shop.</summary>
        public static void PlayBgm(AudioClip clip)
        {
            if (clip == null)
            {
                return;
            }

            Ensure();
            if (_bgm.clip == clip && _bgm.isPlaying)
            {
                return;
            }

            _bgm.clip = clip;
            _bgm.loop = true;
            _bgm.volume = _bgmVolume * BgmBaseGain;
            _bgm.Play();
        }

        /// <summary>The title stays silent (사용자 방향 미정) — leaving to the main menu stops the music.</summary>
        public static void StopBgm()
        {
            Ensure();
            _bgm.Stop();
        }

        public static void PlaySfx(AudioClip clip, float gain)
        {
            if (clip == null)
            {
                return;
            }

            Ensure();
            _sfx.PlayOneShot(clip, gain * _sfxVolume);
        }

        private static void Ensure()
        {
            if (_root != null)
            {
                return;
            }

            _root = new GameObject("GameAudio");
            Object.DontDestroyOnLoad(_root);
            _bgm = _root.AddComponent<AudioSource>();
            _bgm.playOnAwake = false;
            _bgm.ignoreListenerPause = true;
            _sfx = _root.AddComponent<AudioSource>();
            _sfx.playOnAwake = false;
            _sfxVolume = PlayerPrefs.GetFloat(SfxKey, 0.8f);
            _bgmVolume = PlayerPrefs.GetFloat(BgmKey, 0.6f);
            _bgm.volume = _bgmVolume * BgmBaseGain;
        }
    }
}
