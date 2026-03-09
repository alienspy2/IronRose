using System;
using System.Collections.Generic;
using RoseEngine;

public class TestComponent : MonoBehaviour
{
    // 기본 타입 (기존 지원)
    public float speed = 5.0f;
    public int health = 100;
    public bool isActive = true;
    public string playerName = "Player";
    public Vector3 position;
    public Color tint = new Color(1, 1, 1, 1);

    // Phase 39-A: 코어 타입 확장
    public Vector4 customVec4;
    public double customDouble = 3.14;
    public byte customByte = 128;

    // Phase 39-B: 배열/리스트
    public float[] speeds = new float[] { 1.0f, 2.0f, 3.0f };
    public List<Vector3> waypoints = new List<Vector3>();

    // Phase 39-C: 씬 오브젝트 참조
    public GameObject target2;

    // Phase 39-D: 중첩 직렬화 구조체
    [Serializable]
    public struct Stats
    {
        public int hp;
        public float speed;
        public Color tint;
    }
    public Stats[] playerStats;
}
