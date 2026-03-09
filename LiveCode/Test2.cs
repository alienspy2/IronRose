using System;
using System.Collections.Generic;
using RoseEngine;

public class Test2 : MonoBehaviour
{
    public Transform hello;

    public override void Update()
    {
        Debug.Log($"{Time.time}");
    }
}
