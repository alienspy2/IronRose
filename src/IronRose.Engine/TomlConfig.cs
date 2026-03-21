// ------------------------------------------------------------
// @file    TomlConfig.cs
// @brief   Tomlyn의 TomlTable을 감싸는 래퍼 클래스. 타입 안전한 Get/Set API와
//          파일 I/O를 제공한다. TomlConfigArray는 TomlTableArray를 감싸며
//          IEnumerable<TomlConfig>를 구현한다.
// @deps    RoseEngine/EditorDebug
// @exports
//   class TomlConfig
//     LoadFile(string, string?): TomlConfig?                 — 파일에서 TOML 로드
//     LoadString(string, string?): TomlConfig?               — 문자열에서 TOML 로드
//     CreateEmpty(): TomlConfig                              — 빈 TomlConfig 생성
//     SaveToFile(string, string?): bool                      — 파일로 저장
//     ToTomlString(): string                                 — TOML 문자열 변환
//     GetString(string, string): string                      — 문자열 값 읽기
//     GetInt(string, int): int                               — 정수 값 읽기
//     GetLong(string, long): long                            — long 값 읽기
//     GetFloat(string, float): float                         — float 값 읽기
//     GetDouble(string, double): double                      — double 값 읽기
//     GetBool(string, bool): bool                            — bool 값 읽기
//     GetSection(string): TomlConfig?                        — 하위 테이블 래핑 반환
//     GetArray(string): TomlConfigArray?                     — 테이블 배열 래핑 반환
//     GetValues(string): IReadOnlyList<object>?              — 값 배열 반환
//     SetValue(string, object): void                         — 값 설정
//     SetSection(string, TomlConfig): void                   — 하위 섹션 설정
//     SetArray(string, TomlConfigArray): void                — 테이블 배열 설정
//     HasKey(string): bool                                   — 키 존재 여부
//     Remove(string): bool                                   — 키 제거
//     Keys: IEnumerable<string>                              — 전체 키 목록
//     GetRawTable(): TomlTable                               — 내부 TomlTable 직접 반환
//   class TomlConfigArray : IEnumerable<TomlConfig>
//     Count: int                                             — 항목 수
//     this[int]: TomlConfig                                  — 인덱스 접근
//     Add(TomlConfig): void                                  — 항목 추가
//     GetRawArray(): TomlTableArray                          — 내부 배열 직접 반환
// @note    TomlConfig 생성자는 internal로, 같은 어셈블리 내 TomlConfigArray 등에서
//          사용 가능. 외부 어셈블리에서는 팩토리 메서드로만 생성 가능.
// ------------------------------------------------------------
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Tomlyn;
using Tomlyn.Model;
using RoseEngine;

namespace IronRose.Engine
{
    /// <summary>
    /// Tomlyn의 TomlTable을 감싸는 래퍼 클래스.
    /// 타입 안전한 Get/Set API와 파일 I/O를 제공한다.
    /// </summary>
    public class TomlConfig
    {
        private readonly TomlTable _table;

        /// <summary>
        /// 내부 생성자. 같은 어셈블리 내에서만 직접 생성 가능.
        /// 외부에서는 LoadFile, LoadString, CreateEmpty 팩토리 메서드를 사용한다.
        /// </summary>
        internal TomlConfig(TomlTable table)
        {
            _table = table;
        }

        #region 정적 팩토리 메서드

        /// <summary>파일에서 TOML을 로드한다. 파일이 없거나 파싱 실패 시 null 반환.</summary>
        public static TomlConfig? LoadFile(string filePath, string? logTag = null)
        {
            if (!File.Exists(filePath))
            {
                if (logTag != null)
                    EditorDebug.Log($"{logTag} File not found: {filePath}");
                return null;
            }

            try
            {
                var text = File.ReadAllText(filePath);
                var table = Toml.ToModel(text);
                return new TomlConfig(table);
            }
            catch (Exception ex)
            {
                if (logTag != null)
                    EditorDebug.LogWarning($"{logTag} Failed to parse {filePath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>TOML 문자열에서 로드한다. 파싱 실패 시 null 반환.</summary>
        public static TomlConfig? LoadString(string tomlString, string? logTag = null)
        {
            try
            {
                var table = Toml.ToModel(tomlString);
                return new TomlConfig(table);
            }
            catch (Exception ex)
            {
                if (logTag != null)
                    EditorDebug.LogWarning($"{logTag} Failed to parse TOML string: {ex.Message}");
                return null;
            }
        }

        /// <summary>빈 TomlConfig를 생성한다.</summary>
        public static TomlConfig CreateEmpty()
        {
            return new TomlConfig(new TomlTable());
        }

        #endregion

        #region 저장 메서드

        /// <summary>TOML 파일로 저장. 디렉토리 자동 생성. 성공 여부 반환.</summary>
        public bool SaveToFile(string filePath, string? logTag = null)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(filePath, Toml.FromModel(_table));
                return true;
            }
            catch (Exception ex)
            {
                if (logTag != null)
                    EditorDebug.LogWarning($"{logTag} Failed to save {filePath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>TOML 문자열로 변환.</summary>
        public string ToTomlString()
        {
            return Toml.FromModel(_table);
        }

        #endregion

        #region 값 읽기

        /// <summary>문자열 값을 읽는다. 키가 없거나 타입 불일치 시 기본값 반환.</summary>
        public string GetString(string key, string defaultValue = "")
        {
            if (_table.TryGetValue(key, out var val) && val is string s)
                return s;
            return defaultValue;
        }

        /// <summary>int 값을 읽는다. 키가 없거나 타입 불일치 시 기본값 반환.</summary>
        public int GetInt(string key, int defaultValue = 0)
        {
            if (_table.TryGetValue(key, out var val))
            {
                return val switch
                {
                    long l => (int)l,
                    double d => (int)d,
                    _ => defaultValue,
                };
            }
            return defaultValue;
        }

        /// <summary>long 값을 읽는다. 키가 없거나 타입 불일치 시 기본값 반환.</summary>
        public long GetLong(string key, long defaultValue = 0)
        {
            if (_table.TryGetValue(key, out var val))
            {
                return val switch
                {
                    long l => l,
                    double d => (long)d,
                    _ => defaultValue,
                };
            }
            return defaultValue;
        }

        /// <summary>float 값을 읽는다. 키가 없거나 타입 불일치 시 기본값 반환.</summary>
        public float GetFloat(string key, float defaultValue = 0f)
        {
            if (_table.TryGetValue(key, out var val))
            {
                return val switch
                {
                    double d => (float)d,
                    long l => (float)l,
                    float f => f,
                    _ => defaultValue,
                };
            }
            return defaultValue;
        }

        /// <summary>double 값을 읽는다. 키가 없거나 타입 불일치 시 기본값 반환.</summary>
        public double GetDouble(string key, double defaultValue = 0.0)
        {
            if (_table.TryGetValue(key, out var val))
            {
                return val switch
                {
                    double d => d,
                    long l => (double)l,
                    _ => defaultValue,
                };
            }
            return defaultValue;
        }

        /// <summary>bool 값을 읽는다. 키가 없거나 타입 불일치 시 기본값 반환.</summary>
        public bool GetBool(string key, bool defaultValue = false)
        {
            if (_table.TryGetValue(key, out var val) && val is bool b)
                return b;
            return defaultValue;
        }

        #endregion

        #region 중첩 구조 접근

        /// <summary>하위 테이블(섹션)을 TomlConfig로 래핑하여 반환. 없으면 null.</summary>
        public TomlConfig? GetSection(string key)
        {
            if (_table.TryGetValue(key, out var val) && val is TomlTable t)
                return new TomlConfig(t);
            return null;
        }

        /// <summary>테이블 배열을 TomlConfigArray로 반환. 없으면 null.</summary>
        public TomlConfigArray? GetArray(string key)
        {
            if (_table.TryGetValue(key, out var val) && val is TomlTableArray ta)
                return new TomlConfigArray(ta);
            return null;
        }

        /// <summary>값 배열(TomlArray)을 IReadOnlyList&lt;object&gt;로 반환. 없으면 null.</summary>
        public IReadOnlyList<object>? GetValues(string key)
        {
            if (_table.TryGetValue(key, out var val) && val is TomlArray arr)
            {
                var list = new List<object>(arr.Count);
                foreach (var item in arr)
                {
                    if (item != null)
                        list.Add(item);
                }
                return list;
            }
            return null;
        }

        #endregion

        #region 값 쓰기

        /// <summary>값을 설정한다. value는 string, long, double, bool, TomlTable, TomlTableArray, TomlArray.</summary>
        public void SetValue(string key, object value)
        {
            _table[key] = value;
        }

        /// <summary>TomlConfig를 하위 섹션으로 설정한다.</summary>
        public void SetSection(string key, TomlConfig section)
        {
            _table[key] = section._table;
        }

        /// <summary>TomlConfigArray를 테이블 배열로 설정한다.</summary>
        public void SetArray(string key, TomlConfigArray array)
        {
            _table[key] = array.GetRawArray();
        }

        #endregion

        #region 유틸

        /// <summary>키가 존재하는지 확인한다.</summary>
        public bool HasKey(string key) => _table.ContainsKey(key);

        /// <summary>키를 제거한다. 제거 성공 여부를 반환.</summary>
        public bool Remove(string key) => _table.Remove(key);

        /// <summary>모든 키 목록.</summary>
        public IEnumerable<string> Keys => _table.Keys;

        /// <summary>내부 TomlTable을 직접 반환 (점진적 마이그레이션용).</summary>
        public TomlTable GetRawTable() => _table;

        #endregion
    }

    /// <summary>
    /// Tomlyn의 TomlTableArray를 감싸는 래퍼 클래스.
    /// IEnumerable&lt;TomlConfig&gt;를 구현하여 foreach 사용이 가능하다.
    /// </summary>
    public class TomlConfigArray : IEnumerable<TomlConfig>
    {
        private readonly TomlTableArray _array;

        internal TomlConfigArray(TomlTableArray array)
        {
            _array = array;
        }

        public TomlConfigArray()
        {
            _array = new TomlTableArray();
        }

        /// <summary>항목 수.</summary>
        public int Count => _array.Count;

        /// <summary>인덱스로 TomlConfig를 반환한다.</summary>
        public TomlConfig this[int index] => new TomlConfig(_array[index]);

        /// <summary>TomlConfig를 배열에 추가한다.</summary>
        public void Add(TomlConfig config) => _array.Add(config.GetRawTable());

        public IEnumerator<TomlConfig> GetEnumerator()
        {
            for (int i = 0; i < _array.Count; i++)
                yield return new TomlConfig(_array[i]);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>내부 TomlTableArray 직접 접근 (마이그레이션용).</summary>
        public TomlTableArray GetRawArray() => _array;
    }
}
