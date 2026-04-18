// ------------------------------------------------------------
// @file    EditorAssetSelection.cs
// @brief   에디터의 에셋 브라우저 전역 선택 상태. Project 패널, Inspector,
//          CLI(asset.select 계열)가 공유한다. 경로(string) 기반 멀티셀렉션.
// @deps    RoseEngine/ThreadGuard — public API 진입부에서 메인 스레드 가드 수행.
//          경로 유효성 검증은 호출자가 담당한다.
// @exports
//   static class EditorAssetSelection
//     PrimaryPath: string?                              — 마지막 클릭/선택된 에셋 경로 (Primary)
//     Paths: IReadOnlyCollection<string>                — 선택된 모든 경로 (순서 보존)
//     Count: int                                        — 선택 개수
//     SelectionVersion: long                            — 변경 감지용 카운터 (변경 시에만 증가)
//     SelectionChanged: event Action?                   — 상태가 실제 바뀌었을 때 발화
//     Select(string): void                              — 단일 선택으로 교체
//     SelectMany(IEnumerable<string>): void             — 여러 개로 교체 (마지막이 Primary)
//     Add(string): void                                 — 기존 선택에 추가 (중복 시 Primary만 갱신)
//     Remove(string): void                              — 해당 경로 해제 (Primary면 다음 후보로 교체)
//     Clear(): void                                     — 전체 해제
//     Contains(string): bool                            — 포함 여부
// @note    경로는 내부적으로 Normalize()로 정규화(역슬래시→슬래시, 양끝 공백 제거).
//          Null/빈 문자열은 무시된다. thread-safe 아님 — 에디터 메인 스레드에서만 호출할 것.
//          모든 public 쓰기/조회 API(Contains/Select/SelectMany/Add/Remove/Clear)는
//          ThreadGuard.CheckMainThread 로 가드된다. 위반 시 LogError 후 조기 반환.
//          SelectionChanged는 SelectionVersion이 실제로 증가한 경우에만 발화한다.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// 에디터 전역 에셋 선택 상태. 프로젝트 패널 클릭, CLI(asset.select 계열) 등
    /// 모든 진입점이 이곳을 읽고 쓴다. 경로 기반 멀티셀렉션 — 마지막 선택이 Primary.
    /// </summary>
    public static class EditorAssetSelection
    {
        private static readonly List<string> _paths = new();
        private static readonly HashSet<string> _pathSet = new(StringComparer.Ordinal);

        /// <summary>선택 변경 버전 (변경 시에만 증가).</summary>
        public static long SelectionVersion { get; private set; }

        /// <summary>선택 상태가 실제로 바뀌었을 때 발화한다.</summary>
        public static event Action? SelectionChanged;

        /// <summary>Primary (마지막 선택) 경로. 없으면 null.</summary>
        public static string? PrimaryPath => _paths.Count > 0 ? _paths[^1] : null;

        /// <summary>선택된 모든 경로 (선택된 순서, 마지막이 Primary).</summary>
        public static IReadOnlyCollection<string> Paths => _paths;

        /// <summary>선택된 항목 수.</summary>
        public static int Count => _paths.Count;

        /// <summary>O(1) 포함 여부. 메인 스레드 전용 (내부 컬렉션이 lock-free).</summary>
        public static bool Contains(string path)
        {
            if (!ThreadGuard.CheckMainThread("EditorAssetSelection.Contains")) return false;
            var normalized = Normalize(path);
            return normalized != null && _pathSet.Contains(normalized);
        }

        /// <summary>단일 선택으로 교체. null/empty면 Clear와 동일. 메인 스레드 전용.</summary>
        public static void Select(string path)
        {
            if (!ThreadGuard.CheckMainThread("EditorAssetSelection.Select")) return;

            var normalized = Normalize(path);
            if (normalized == null)
            {
                Clear();
                return;
            }

            // 이미 그 단일 경로만 선택된 상태라면 no-op.
            if (_paths.Count == 1 && _paths[0] == normalized)
                return;

            _paths.Clear();
            _pathSet.Clear();
            _paths.Add(normalized);
            _pathSet.Add(normalized);
            BumpAndNotify();
        }

        /// <summary>여러 개로 교체. 순서대로 추가하며 마지막이 Primary가 된다. 메인 스레드 전용.</summary>
        public static void SelectMany(IEnumerable<string> paths)
        {
            if (!ThreadGuard.CheckMainThread("EditorAssetSelection.SelectMany")) return;

            if (paths == null)
            {
                Clear();
                return;
            }

            var newList = new List<string>();
            var newSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in paths)
            {
                var normalized = Normalize(p);
                if (normalized == null) continue;
                if (newSet.Add(normalized))
                    newList.Add(normalized);
            }

            // 동일 상태면 no-op.
            if (newList.Count == _paths.Count && newList.SequenceEqual(_paths))
                return;

            _paths.Clear();
            _pathSet.Clear();
            foreach (var p in newList)
            {
                _paths.Add(p);
                _pathSet.Add(p);
            }
            BumpAndNotify();
        }

        /// <summary>기존 선택에 추가. 이미 있으면 Primary로 끌어올린다. 메인 스레드 전용.</summary>
        public static void Add(string path)
        {
            if (!ThreadGuard.CheckMainThread("EditorAssetSelection.Add")) return;

            var normalized = Normalize(path);
            if (normalized == null) return;

            if (_pathSet.Contains(normalized))
            {
                // 이미 있지만 Primary가 아니면 Primary로 이동.
                if (_paths[^1] == normalized) return;
                _paths.Remove(normalized);
                _paths.Add(normalized);
                BumpAndNotify();
                return;
            }

            _paths.Add(normalized);
            _pathSet.Add(normalized);
            BumpAndNotify();
        }

        /// <summary>선택 해제. 없으면 no-op. 메인 스레드 전용.</summary>
        public static void Remove(string path)
        {
            if (!ThreadGuard.CheckMainThread("EditorAssetSelection.Remove")) return;

            var normalized = Normalize(path);
            if (normalized == null) return;
            if (!_pathSet.Remove(normalized)) return;
            _paths.Remove(normalized);
            BumpAndNotify();
        }

        /// <summary>전체 해제. 이미 비어있으면 no-op. 메인 스레드 전용.</summary>
        public static void Clear()
        {
            if (!ThreadGuard.CheckMainThread("EditorAssetSelection.Clear")) return;

            if (_paths.Count == 0) return;
            _paths.Clear();
            _pathSet.Clear();
            BumpAndNotify();
        }

        private static string? Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var trimmed = path.Trim().Replace('\\', '/');
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static void BumpAndNotify()
        {
            SelectionVersion++;
            SelectionChanged?.Invoke();
        }
    }
}
