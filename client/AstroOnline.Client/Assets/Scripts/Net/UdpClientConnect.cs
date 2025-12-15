using System.Collections.Generic;
using UnityEngine;

public sealed class UdpClientConnect : MonoBehaviour
{
    public NetClient Client => _client;

    public float LastInterpAvgError { get; private set; }

    private NetClient _client;

    private const ulong InterpDelayTicks = 3;

    private static readonly float MoveSpeed = ProtocolConstants.MoveSpeed;
    private static readonly float TickDt = ProtocolConstants.TickDt;

    // Reconciliation tolerance (units). Below this, do not rebuild prediction.
    private const float ReconcileEpsilon = 0.02f;

    private readonly Dictionary<ulong, List<NetSnapshot>> _historyByClient = new();
    private readonly Dictionary<ulong, GameObject> _entityByClient = new();

    private uint _localSeq;
    private uint _lastAckSeq;
    private bool _hasAckBase;

    private Vector3 _authoritativeBasePos;
    private Vector3 _predictedPos;

    private readonly Dictionary<uint, InputSample> _inputBySeq = new();

    private float _accum;

    private bool _terminalStop;
    private string _terminalStopMessage;

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
            _terminalStopMessage ??= "Client out of date. Update required.";
            Debug.LogError(_terminalStopMessage);
        }
    }

    private void OnNetRejected(RejectReason reason, byte expected, byte got)
    {
        if (reason == RejectReason.ProtocolVersionMismatch && !_terminalStop)
        {
            _terminalStop = true;
            _terminalStopMessage =
                $"Client out of date (expectedVersion={expected}, gotVersion={got}). Update required.";
            Debug.LogError(_terminalStopMessage);
        }
    }

    private void Update()
    {
        if (_client == null || _terminalStop)
            return;

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

            if (_client.IsConnected && _client.ClientId != 0 && s.ClientId == _client.ClientId)
            {
                var serverPos = new Vector3(s.X, s.Y, s.Z);
                uint newAck = s.LastInputSeqAck;

                if (!_hasAckBase)
                {
                    _hasAckBase = true;
                    _lastAckSeq = newAck;
                    _authoritativeBasePos = serverPos;
                    _predictedPos = serverPos;
                    _inputBySeq.Clear();
                    _localSeq = newAck;
                    _accum = 0f;
                }
                else if (newAck > _lastAckSeq)
                {
                    _lastAckSeq = newAck;

                    float err = Vector3.Distance(_predictedPos, serverPos);
                    _authoritativeBasePos = serverPos;

                    if (err > ReconcileEpsilon)
                        ReplayFromAck();
                }
            }
        }

        if (!_client.IsConnected || !_hasAckBase || _client.ClientId == 0)
        {
            RenderFromInterpolationOnly();
            SampleInterpMetricOncePerSecond();
            return;
        }

        float inputX = 0f;
        float inputZ = 0f;
        if (Input.GetKey(KeyCode.A)) inputX -= 1f;
        if (Input.GetKey(KeyCode.D)) inputX += 1f;
        if (Input.GetKey(KeyCode.W)) inputZ += 1f;
        if (Input.GetKey(KeyCode.S)) inputZ -= 1f;

        _accum += Time.unscaledDeltaTime;

        while (_accum >= TickDt)
        {
            _accum -= TickDt;
            _localSeq++;

            _inputBySeq[_localSeq] = new InputSample(inputX, inputZ);
            Integrate(ref _predictedPos, inputX, inputZ);
            _client.SendInput(_localSeq, inputX, inputZ);
        }

        RenderAll();
        SampleInterpMetricOncePerSecond();
    }

    private void SampleInterpMetricOncePerSecond()
    {
        if (Time.time < _nextInterpSampleTime)
            return;

        _nextInterpSampleTime = Time.time + 1f;
        LastInterpAvgError = _interpErrorSamples > 0
            ? _interpErrorAccum / _interpErrorSamples
            : 0f;

        _interpErrorAccum = 0f;
        _interpErrorSamples = 0;
    }

    private void ReplayFromAck()
    {
        _predictedPos = _authoritativeBasePos;

        for (uint seq = _lastAckSeq + 1; seq <= _localSeq; seq++)
        {
            if (_inputBySeq.TryGetValue(seq, out var inp))
                Integrate(ref _predictedPos, inp.X, inp.Z);
            else
                Integrate(ref _predictedPos, 0f, 0f);
        }

        var toRemove = new List<uint>();
        foreach (var k in _inputBySeq.Keys)
            if (k <= _lastAckSeq) toRemove.Add(k);

        for (int i = 0; i < toRemove.Count; i++)
            _inputBySeq.Remove(toRemove[i]);
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
                pos = _predictedPos;
            }
            else
            {
                if (!TryGetInterpolated(hist, out _, out var x, out var y, out var z))
                    continue;

                pos = new Vector3(x, y, z);

                var last = hist[hist.Count - 1];
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
            var hist = kv.Value;
            if (hist.Count == 0)
                continue;

            if (!TryGetInterpolated(hist, out _, out var x, out var y, out var z))
                continue;

            GetOrCreateEntity(kv.Key).transform.position =
                new Vector3(x, y, z);
        }
    }

    private static void Integrate(ref Vector3 pos, float inputX, float inputZ)
    {
        float magSq = (inputX * inputX) + (inputZ * inputZ);
        if (magSq > 1f)
        {
            float invMag = 1f / Mathf.Sqrt(magSq);
            inputX *= invMag;
            inputZ *= invMag;
        }

        pos.x += inputX * MoveSpeed * TickDt;
        pos.z += inputZ * MoveSpeed * TickDt;
    }

    private GameObject GetOrCreateEntity(ulong clientId)
    {
        if (_entityByClient.TryGetValue(clientId, out var go) && go != null)
            return go;

        go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = $"NetEntity_{clientId}";
        _entityByClient[clientId] = go;
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

    private static bool TryGetInterpolated(
        List<NetSnapshot> hist,
        out ulong renderTick,
        out float x,
        out float y,
        out float z)
    {
        var latest = hist[^1];
        renderTick = latest.ServerTick > InterpDelayTicks
            ? latest.ServerTick - InterpDelayTicks
            : 0;

        if (renderTick <= hist[0].ServerTick)
        {
            x = hist[0].X; y = hist[0].Y; z = hist[0].Z;
            return true;
        }

        if (renderTick >= latest.ServerTick)
        {
            x = latest.X; y = latest.Y; z = latest.Z;
            return true;
        }

        NetSnapshot a = hist[0], b = latest;

        for (int i = 0; i < hist.Count - 1; i++)
        {
            var s0 = hist[i];
            var s1 = hist[i + 1];
            if (s0.ServerTick <= renderTick && renderTick <= s1.ServerTick)
            {
                a = s0; b = s1; break;
            }
        }

        ulong dt = b.ServerTick - a.ServerTick;
        if (dt == 0)
        {
            x = a.X; y = a.Y; z = a.Z;
            return true;
        }

        float alpha = (float)(renderTick - a.ServerTick) / dt;
        x = Mathf.LerpUnclamped(a.X, b.X, alpha);
        y = Mathf.LerpUnclamped(a.Y, b.Y, alpha);
        z = Mathf.LerpUnclamped(a.Z, b.Z, alpha);
        return true;
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
