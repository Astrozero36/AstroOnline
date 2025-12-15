using System.Collections.Generic;
using UnityEngine;

public sealed class UdpClientConnect : MonoBehaviour
{
    public GameObject? LocalPlayerObject { get; private set; }
    public NetClient Client => _client;
    public float LastInterpAvgError { get; private set; }

    private NetClient _client;

    private const ulong InterpDelayTicks = 3;
    private static readonly float MoveSpeed = ProtocolConstants.MoveSpeed;
    private static readonly float TickDt = ProtocolConstants.TickDt;

    // Reconciliation
    private const float ReconcileEpsilon = 0.02f;     // ignore tiny errors
    private const float MaxSnapError = 1.0f;          // emergency snap (teleport / desync)
    private const float ReconcileHalfLife = 0.08f;    // seconds (how fast error decays)

    // Interpolation back-time (ms)
    private static readonly uint InterpBackTimeMs =
        (uint)Mathf.RoundToInt(InterpDelayTicks * TickDt * 1000f);

    private readonly Dictionary<ulong, List<NetSnapshot>> _historyByClient = new();
    private readonly Dictionary<ulong, GameObject> _entityByClient = new();

    // Prediction state
    private uint _localSeq;
    private uint _lastAckSeq;
    private bool _hasAckBase;

    private Vector3 _predictedPos;

    // Soft correction accumulator (applied over time)
    private Vector3 _reconcileOffset;

    private readonly Dictionary<uint, InputSample> _inputBySeq = new();

    // Fixed-tick accumulator
    private float _accum;

    // Frame-sampled input
    private Vector2 _pendingInput;

    // Terminal stop
    private bool _terminalStop;

    // Interp metrics
    private float _interpErrorAccum;
    private int _interpErrorSamples;
    private float _nextInterpSampleTime;

    private void Start()
    {
        var config = new NetClientConfig
        {
            ServerIp = "127.0.0.1",
            ServerPort = 7777,
            PingIntervalMs = 1000
        };

        _client = new NetClient(config);
        _client.OnStateChanged += OnNetStateChanged;
        _client.OnRejected += OnNetRejected;
        _client.Start();

        _nextInterpSampleTime = Time.time + 1f;
        LastInterpAvgError = 0f;
    }

    private void OnNetStateChanged(ConnectionState state)
    {
        if (state == ConnectionState.UpdateRequired && !_terminalStop)
        {
            _terminalStop = true;
            Debug.LogError("Client out of date. Update required.");
        }
    }

    private void OnNetRejected(RejectReason reason, byte expected, byte got)
    {
        if (reason == RejectReason.ProtocolVersionMismatch && !_terminalStop)
        {
            _terminalStop = true;
            Debug.LogError(
                $"Client out of date (expected={expected}, got={got}). Update required.");
        }
    }

    private void Update()
    {
        if (_client == null || _terminalStop)
            return;

        // -------- FRAME INPUT SAMPLING --------
        float ix = 0f;
        float iz = 0f;
        if (Input.GetKey(KeyCode.A)) ix -= 1f;
        if (Input.GetKey(KeyCode.D)) ix += 1f;
        if (Input.GetKey(KeyCode.W)) iz += 1f;
        if (Input.GetKey(KeyCode.S)) iz -= 1f;
        _pendingInput = new Vector2(ix, iz);

        // -------- SNAPSHOT DRAIN --------
        while (_client.TryDequeueSnapshot(out var s))
        {
            if (!_historyByClient.TryGetValue(s.ClientId, out var list))
            {
                list = new List<NetSnapshot>(32);
                _historyByClient.Add(s.ClientId, list);
            }

            InsertSortedByTick(list, s);
            if (list.Count > 64)
                list.RemoveRange(0, list.Count - 64);

            // Local reconciliation
            if (_client.IsConnected &&
                _client.ClientId != 0 &&
                s.ClientId == _client.ClientId)
            {
                var serverPos = new Vector3(s.X, s.Y, s.Z);
                uint newAck = s.LastInputSeqAck;

                if (!_hasAckBase)
                {
                    _hasAckBase = true;
                    _lastAckSeq = newAck;
                    _predictedPos = serverPos;
                    _reconcileOffset = Vector3.zero;
                    _inputBySeq.Clear();
                    _localSeq = newAck;
                    _accum = 0f;
                }
                else if (newAck > _lastAckSeq)
                {
                    _lastAckSeq = newAck;

                    Vector3 current = _predictedPos + _reconcileOffset;
                    Vector3 delta = serverPos - current;
                    float err = delta.magnitude;

                    if (err > MaxSnapError)
                    {
                        // catastrophic desync -> snap
                        _predictedPos = serverPos;
                        _reconcileOffset = Vector3.zero;
                        _inputBySeq.Clear();
                        _localSeq = newAck;
                    }
                    else if (err > ReconcileEpsilon)
                    {
                        // accumulate soft correction
                        _reconcileOffset += delta;
                    }

                    DropAckedInputs();
                }
            }
        }

        if (!_client.IsConnected || !_hasAckBase || _client.ClientId == 0)
        {
            RenderFromInterpolationOnly();
            SampleInterpMetricOncePerSecond();
            return;
        }

        // -------- FIXED TICK --------
        _accum += Time.unscaledDeltaTime;

        while (_accum >= TickDt)
        {
            _accum -= TickDt;
            _localSeq++;

            float inputX = _pendingInput.x;
            float inputZ = _pendingInput.y;

            _inputBySeq[_localSeq] = new InputSample(inputX, inputZ);
            Integrate(ref _predictedPos, inputX, inputZ);
            _client.SendInput(_localSeq, inputX, inputZ);

            // Apply soft reconciliation decay (critically damped)
            if (_reconcileOffset.sqrMagnitude > 0f)
            {
                float k = Mathf.Exp(-Mathf.Log(2f) * TickDt / ReconcileHalfLife);
                _reconcileOffset *= k;
            }
        }

        RenderAll();
        SampleInterpMetricOncePerSecond();
    }

    private void DropAckedInputs()
    {
        var toRemove = new List<uint>();
        foreach (var k in _inputBySeq.Keys)
            if (k <= _lastAckSeq)
                toRemove.Add(k);

        for (int i = 0; i < toRemove.Count; i++)
            _inputBySeq.Remove(toRemove[i]);
    }

    private void SampleInterpMetricOncePerSecond()
    {
        if (Time.time < _nextInterpSampleTime)
            return;

        _nextInterpSampleTime = Time.time + 1f;
        LastInterpAvgError =
            _interpErrorSamples > 0
                ? _interpErrorAccum / _interpErrorSamples
                : 0f;

        _interpErrorAccum = 0f;
        _interpErrorSamples = 0;
    }

    private void RenderAll()
    {
        foreach (var kv in _historyByClient)
        {
            ulong cid = kv.Key;
            var hist = kv.Value;
            if (hist.Count == 0)
                continue;

            Vector3 pos;

            if (cid == _client.ClientId)
            {
                pos = _predictedPos + _reconcileOffset;
            }
            else
            {
                if (!TryGetInterpolated(hist, out pos))
                    continue;

                var last = hist[^1];
                float err = Vector3.Distance(
                    new Vector3(last.X, last.Y, last.Z), pos);
                _interpErrorAccum += err;
                _interpErrorSamples++;
            }

            GetOrCreateEntity(cid).transform.position = pos;
        }
    }

    private void RenderFromInterpolationOnly()
    {
        foreach (var kv in _historyByClient)
        {
            if (!TryGetInterpolated(kv.Value, out var pos))
                continue;

            GetOrCreateEntity(kv.Key).transform.position = pos;
        }
    }

    private static bool TryGetInterpolated(List<NetSnapshot> hist, out Vector3 pos)
    {
        var latest = hist[^1];

        if (latest.ServerTimeMs != 0 && hist[0].ServerTimeMs != 0)
        {
            uint renderTimeMs = unchecked(latest.ServerTimeMs - InterpBackTimeMs);

            if (TimeLE(renderTimeMs, hist[0].ServerTimeMs))
            {
                pos = new Vector3(hist[0].X, hist[0].Y, hist[0].Z);
                return true;
            }

            if (TimeGE(renderTimeMs, latest.ServerTimeMs))
            {
                pos = new Vector3(latest.X, latest.Y, latest.Z);
                return true;
            }

            NetSnapshot a = hist[0], b = latest;
            for (int i = 0; i < hist.Count - 1; i++)
            {
                if (TimeLE(hist[i].ServerTimeMs, renderTimeMs) &&
                    TimeLE(renderTimeMs, hist[i + 1].ServerTimeMs))
                {
                    a = hist[i];
                    b = hist[i + 1];
                    break;
                }
            }

            uint dt = unchecked(b.ServerTimeMs - a.ServerTimeMs);
            float alpha = dt == 0
                ? 0f
                : (float)unchecked(renderTimeMs - a.ServerTimeMs) / dt;

            pos = Vector3.LerpUnclamped(
                new Vector3(a.X, a.Y, a.Z),
                new Vector3(b.X, b.Y, b.Z),
                alpha);
            return true;
        }

        pos = Vector3.zero;
        return false;
    }

    private static bool TimeLE(uint a, uint b) => unchecked((int)(a - b)) <= 0;
    private static bool TimeGE(uint a, uint b) => unchecked((int)(a - b)) >= 0;

    private static void Integrate(ref Vector3 pos, float ix, float iz)
    {
        float magSq = (ix * ix) + (iz * iz);
        if (magSq > 1f)
        {
            float inv = 1f / Mathf.Sqrt(magSq);
            ix *= inv;
            iz *= inv;
        }

        pos.x += ix * MoveSpeed * TickDt;
        pos.z += iz * MoveSpeed * TickDt;
    }

    private GameObject GetOrCreateEntity(ulong clientId)
    {
        if (_entityByClient.TryGetValue(clientId, out var go) && go != null)
            return go;

        go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = $"NetEntity_{clientId}";
        _entityByClient[clientId] = go;

        if (_client != null && _client.IsConnected && clientId == _client.ClientId)
        {
            LocalPlayerObject = go;
        }

        return go;
    }


    private static void InsertSortedByTick(List<NetSnapshot> list, NetSnapshot s)
    {
        if (list.Count == 0 || s.ServerTick >= list[^1].ServerTick)
        {
            list.Add(s);
            return;
        }

        for (int i = list.Count - 1; i >= 0; i--)
            if (s.ServerTick >= list[i].ServerTick)
            {
                list.Insert(i + 1, s);
                return;
            }

        list.Insert(0, s);
    }

    private void OnDestroy()
    {
        _client?.Dispose();
    }

    private readonly struct InputSample
    {
        public readonly float X;
        public readonly float Z;
        public InputSample(float x, float z) { X = x; Z = z; }
    }
}
