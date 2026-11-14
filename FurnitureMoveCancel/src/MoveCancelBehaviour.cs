using System;
using UnityEngine;

namespace FurnitureMoveCancel;

/// <summary>Drives MoveCancel every frame. Registered into IL2CPP by BasePlugin.AddComponent.</summary>
public class MoveCancelBehaviour : MonoBehaviour
{
    public MoveCancelBehaviour(IntPtr ptr) : base(ptr) { }

    private void Update()
    {
        if (!Plugin.Active) return;
        MoveCancel.Tick();
    }
}
