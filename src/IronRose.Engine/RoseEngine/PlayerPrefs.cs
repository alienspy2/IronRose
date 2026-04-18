// ------------------------------------------------------------
// @file    PlayerPrefs.cs
// @brief   Unity 호환 PlayerPrefs API. TOML 포맷으로 사용자 홈 디렉토리에 저장.
//          에디터와 런타임이 같은 파일을 공유한다.
// @deps    IronRose.Engine/ProjectContext, IronRose.Engine/TomlConfig, RoseEngine/Debug
// @exports
//   static class PlayerPrefs
//     SetInt(string, int): void
//     GetInt(string, int): int
//     SetFloat(string, float): void
//     GetFloat(string, float): float
//     SetString(string, string): void
//     GetString(string, string): string
//     HasKey(string): bool
//     DeleteKey(string): void
//     DeleteAll(): void
//     Save(): void
// @note    값은 메모리에 캐시되며 Save() 호출 시 디스크에 기록된다.
//          앱 종료 시 자동으로 Save()가 호출된다.
//          스레드 안전성: _data 접근은 lock으로 보호된다.
//          Save()/Shutdown()은 snapshot 패턴 — lock 안에서 _data 얕은 복사본을 만든 뒤
//          파일 I/O 는 lock 밖에서 수행한다. 파일 쓰기 실패 시 _dirty 가 복구되어 재시도 가능.
//          TOML I/O는 TomlConfig 래퍼 API만 사용한다.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using IronRose.Engine;

namespace RoseEngine
{
    public static class PlayerPrefs
    {
        private enum PrefType { Int, Float, String }
        private readonly record struct PrefEntry(PrefType Type, object Value);

        private static readonly Dictionary<string, PrefEntry> _data = new();
        private static readonly object _lock = new();
        private static bool _dirty = false;
        private static bool _loaded = false;

        private const string SECTION_INT = "int";
        private const string SECTION_FLOAT = "float";
        private const string SECTION_STRING = "string";

        #region Set 메서드

        public static void SetInt(string key, int value)
        {
            lock (_lock)
            {
                EnsureLoaded();
                if (string.IsNullOrEmpty(key))
                    throw new ArgumentException("Key cannot be null or empty.", nameof(key));
                _data[key] = new PrefEntry(PrefType.Int, value);
                _dirty = true;
            }
        }

        public static void SetFloat(string key, float value)
        {
            lock (_lock)
            {
                EnsureLoaded();
                if (string.IsNullOrEmpty(key))
                    throw new ArgumentException("Key cannot be null or empty.", nameof(key));
                _data[key] = new PrefEntry(PrefType.Float, value);
                _dirty = true;
            }
        }

        public static void SetString(string key, string value)
        {
            lock (_lock)
            {
                EnsureLoaded();
                if (string.IsNullOrEmpty(key))
                    throw new ArgumentException("Key cannot be null or empty.", nameof(key));
                _data[key] = new PrefEntry(PrefType.String, value);
                _dirty = true;
            }
        }

        #endregion

        #region Get 메서드

        public static int GetInt(string key)
        {
            return GetInt(key, 0);
        }

        public static int GetInt(string key, int defaultValue)
        {
            lock (_lock)
            {
                EnsureLoaded();
                if (!_data.TryGetValue(key, out var entry))
                    return defaultValue;

                return entry.Type switch
                {
                    PrefType.Int => (int)entry.Value,
                    PrefType.Float => (int)(float)entry.Value,
                    _ => defaultValue,
                };
            }
        }

        public static float GetFloat(string key)
        {
            return GetFloat(key, 0f);
        }

        public static float GetFloat(string key, float defaultValue)
        {
            lock (_lock)
            {
                EnsureLoaded();
                if (!_data.TryGetValue(key, out var entry))
                    return defaultValue;

                return entry.Type switch
                {
                    PrefType.Float => (float)entry.Value,
                    PrefType.Int => (float)(int)entry.Value,
                    _ => defaultValue,
                };
            }
        }

        public static string GetString(string key)
        {
            return GetString(key, "");
        }

        public static string GetString(string key, string defaultValue)
        {
            lock (_lock)
            {
                EnsureLoaded();
                if (!_data.TryGetValue(key, out var entry))
                    return defaultValue;

                return entry.Type switch
                {
                    PrefType.String => (string)entry.Value,
                    _ => defaultValue,
                };
            }
        }

        #endregion

        #region 관리 메서드

        public static bool HasKey(string key)
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _data.ContainsKey(key);
            }
        }

        public static void DeleteKey(string key)
        {
            lock (_lock)
            {
                EnsureLoaded();
                _data.Remove(key);
                _dirty = true;
            }
        }

        public static void DeleteAll()
        {
            lock (_lock)
            {
                EnsureLoaded();
                _data.Clear();
                _dirty = true;
            }
        }

        public static void Save()
        {
            // 1) lock 안에서는 얕은 복사본만 만든다 (파일 I/O 를 들고 있지 않는다).
            Dictionary<string, PrefEntry> snapshot;
            lock (_lock)
            {
                EnsureLoaded();
                snapshot = new Dictionary<string, PrefEntry>(_data);
                _dirty = false;
            }

            // 2) lock 밖에서 실제 파일 쓰기 수행. 예외 시 _dirty 복구.
            try
            {
                SaveInternal(snapshot);
            }
            catch
            {
                lock (_lock) { _dirty = true; }
                throw;
            }
        }

        #endregion

        #region 내부 생명주기

        internal static void Initialize()
        {
            lock (_lock)
            {
                _data.Clear();
                _dirty = false;
                _loaded = false;
                LoadFromFile();
                _loaded = true;
            }
        }

        internal static void Shutdown()
        {
            Dictionary<string, PrefEntry>? snapshot = null;
            lock (_lock)
            {
                if (_dirty)
                {
                    snapshot = new Dictionary<string, PrefEntry>(_data);
                    _dirty = false;
                }
            }

            if (snapshot != null)
            {
                try
                {
                    SaveInternal(snapshot);
                }
                catch
                {
                    lock (_lock) { _dirty = true; }
                    throw;
                }
            }
        }

        #endregion

        #region Private 메서드

        private static void EnsureLoaded()
        {
            if (!_loaded)
            {
                LoadFromFile();
                _loaded = true;
            }
        }

        private static void LoadFromFile()
        {
            var filePath = GetPrefsFilePath();
            var config = TomlConfig.LoadFile(filePath);
            if (config == null)
                return;  // 파일이 없으면 빈 상태로 시작

            // [int] 섹션
            var intSection = config.GetSection(SECTION_INT);
            if (intSection != null)
            {
                foreach (var key in intSection.Keys)
                {
                    var value = intSection.GetInt(key, 0);
                    _data[key] = new PrefEntry(PrefType.Int, value);
                }
            }

            // [float] 섹션
            var floatSection = config.GetSection(SECTION_FLOAT);
            if (floatSection != null)
            {
                foreach (var key in floatSection.Keys)
                {
                    var value = floatSection.GetFloat(key, 0f);
                    _data[key] = new PrefEntry(PrefType.Float, value);
                }
            }

            // [string] 섹션
            var stringSection = config.GetSection(SECTION_STRING);
            if (stringSection != null)
            {
                foreach (var key in stringSection.Keys)
                {
                    var value = stringSection.GetString(key, "");
                    _data[key] = new PrefEntry(PrefType.String, value);
                }
            }
        }

        /// <summary>
        /// lock 외부에서 호출되는 파일 I/O 경로.
        /// 인자 snapshot 은 호출자가 lock 안에서 확보한 _data 의 얕은 복사본이다.
        /// _data / _dirty 에 직접 접근하지 않는다.
        /// </summary>
        private static void SaveInternal(Dictionary<string, PrefEntry> snapshot)
        {
            var config = TomlConfig.CreateEmpty();

            // 타입별 섹션 생성
            var intSection = TomlConfig.CreateEmpty();
            var floatSection = TomlConfig.CreateEmpty();
            var stringSection = TomlConfig.CreateEmpty();

            foreach (var kvp in snapshot)
            {
                switch (kvp.Value.Type)
                {
                    case PrefType.Int:
                        intSection.SetValue(kvp.Key, (long)(int)kvp.Value.Value);
                        break;
                    case PrefType.Float:
                        floatSection.SetValue(kvp.Key, (double)(float)kvp.Value.Value);
                        break;
                    case PrefType.String:
                        stringSection.SetValue(kvp.Key, (string)kvp.Value.Value);
                        break;
                }
            }

            config.SetSection(SECTION_INT, intSection);
            config.SetSection(SECTION_FLOAT, floatSection);
            config.SetSection(SECTION_STRING, stringSection);

            var filePath = GetPrefsFilePath();
            config.SaveToFile(filePath, "[PlayerPrefs]");
        }

        private static string GetPrefsFilePath()
        {
            var projectName = IronRose.Engine.ProjectContext.ProjectName;
            if (string.IsNullOrEmpty(projectName))
                projectName = "Default";

            var safeName = SanitizeFileName(projectName);

            string baseDir;
            if (OperatingSystem.IsWindows())
            {
                baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "IronRose", "playerprefs");
            }
            else
            {
                baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".ironrose", "playerprefs");
            }

            return Path.Combine(baseDir, safeName + ".toml");
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                    chars[i] = '_';
            }
            return new string(chars);
        }

        #endregion
    }
}
