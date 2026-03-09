using System.Collections.Generic;
using RoseEngine;

namespace IronRose.AssetPipeline
{
    /// <summary>에셋 로드/언로드 추상화 — 테스트 시 대체 구현 가능.</summary>
    public interface IAssetDatabase
    {
        int AssetCount { get; }
        void ScanAssets(string projectPath);
        string? GetPathFromGuid(string guid);
        string? GetGuidFromPath(string path);
        T? Load<T>(string path) where T : class;
        T? LoadByGuid<T>(string guid) where T : class;
        void Unload(string path);
        void UnloadAll();
        void ClearCache();
        string[] GetUncachedAssetPaths();
        void EnsureDiskCached(string path);
        /// <summary>스캔된 모든 에셋 경로를 반환.</summary>
        IReadOnlyCollection<string> GetAllAssetPaths();
        /// <summary>캐시 무효화 + 재임포트.</summary>
        void Reimport(string path);
        /// <summary>Mesh 인스턴스로부터 sub-asset GUID 역검색. null이면 직렬화 무시.</summary>
        string? FindGuidForMesh(Mesh mesh);
        /// <summary>Material 인스턴스로부터 sub-asset GUID 역검색. null이면 직렬화 무시.</summary>
        string? FindGuidForMaterial(Material material);
        /// <summary>Texture2D 인스턴스로부터 sub-asset GUID 역검색.</summary>
        string? FindGuidForTexture(Texture2D texture);
        /// <summary>Sprite 인스턴스로부터 sub-asset GUID 역검색.</summary>
        string? FindGuidForSprite(Sprite sprite);
        /// <summary>파일 경로에 대한 sub-asset 목록 반환.</summary>
        IReadOnlyList<SubAssetEntry> GetSubAssets(string filePath);
        /// <summary>지정 경로의 다음 파일 변경 이벤트를 무시 (자체 쓰기 보호).</summary>
        void SuppressNextChange(string absolutePath);
    }
}
