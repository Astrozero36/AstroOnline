using UnityEngine;

public sealed class CameraFollowSmooth : MonoBehaviour
{
    public float SmoothTime = 0.08f;
    public Vector3 Offset = new Vector3(0, 6, -6);

    private Transform _target;
    private Vector3 _velocity;

    private void LateUpdate()
    {
        if (_target == null)
        {
            TryBindToLocalPlayer();
            return;
        }

        Vector3 desired = _target.position + Offset;
        transform.position = Vector3.SmoothDamp(
            transform.position,
            desired,
            ref _velocity,
            SmoothTime
        );

        transform.LookAt(_target);
    }

    private void TryBindToLocalPlayer()
    {
        var conn = FindFirstObjectByType<UdpClientConnect>();
        if (conn == null)
            return;

        if (conn.LocalPlayerObject == null)
            return;

        _target = conn.LocalPlayerObject.transform;
    }
}
