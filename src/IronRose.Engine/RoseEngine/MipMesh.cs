namespace RoseEngine
{
    /// <summary>
    /// Mipmap과 동일한 개념의 메시 LOD 체인.
    /// lodMeshes[0] = 원본, lodMeshes[1] = 1/2, lodMeshes[2] = 1/4, ...
    /// </summary>
    public class MipMesh
    {
        public Mesh[] lodMeshes = [];

        public int LodCount => lodMeshes.Length;

        /// <summary>LOD 0(원본)을 제외한 LOD 메시의 GPU 리소스 해제.</summary>
        public void Dispose()
        {
            // LOD 0은 MeshImportResult.Mesh와 공유하므로 여기서 Dispose하지 않음
            for (int i = 1; i < lodMeshes.Length; i++)
                lodMeshes[i].Dispose();
        }
    }
}
