#!/usr/bin/env python3
"""One voice message's transcript, in a process of its own: `transcribe.py <audio> '<settings json>'` prints one JSON
line, {"text": ..., "model": ..., "audio_s", "load_s", "transcribe_s", "peak_rss_mb"}.

The bridge runs this as a child so a model's memory goes back to the system when the child exits (2026-11-10).
In-process, faster-whisper's memory was never returned after the model was let go: one 171 s clip left Claude's
bridge at 673 MB resident, and Arden's grew to 2.8 GB and was OOM-killed on an 8 GB box with no swap.

Transcribers (settings "transcriber"):
- "parakeet": NVIDIA Parakeet TDT 0.6B v2, int8 ONNX via onnx-asr, Silero VAD. On John's 9 clips (2026-11-10) it
  ran at 0.14x audio length on 4 cores against Whisper small's 0.29x, kept the most ums (151 to 131), and read about as
  accurately. Needs `parakeet_dir` and `vad_dir`.
- "whisper": faster-whisper (`whisper_model`, `whisper_dir`), with a prompt in John's manner to keep fillers.
"""
import json
import resource
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import roomcore as rc

FILLER_PROMPT = "Um, so, uh, I was gonna say, like, um... you know, uh, yeah."
SAMPLE_RATE = 16000


def whisper_words(path, s):
    from faster_whisper import WhisperModel
    t0 = time.time()
    d = s.get("whisper_dir")
    model = WhisperModel(s.get("whisper_model", "small"), device="cpu", compute_type="int8", cpu_threads=s["threads"],
                         download_root=d if d and Path(d).is_dir() else None)
    t1 = time.time()
    # The prompt keeps the fillers and restarts that Whisper otherwise cleans away: measured on John's clip
    # 1569616107520790648, it brought back every um and "y'all, y'all" and kept the punctuation.
    segments, info = model.transcribe(str(path), vad_filter=True, word_timestamps=True, initial_prompt=FILLER_PROMPT)
    words = [(w.start, w.end, w.word) for seg in segments for w in (seg.words or [])]
    return words, info.duration, t1 - t0, time.time() - t1, "whisper-" + s.get("whisper_model", "small")


def parakeet_words(path, s):
    import onnx_asr
    import onnxruntime as ort
    from faster_whisper.audio import decode_audio     # PyAV underneath; Discord's voice messages are Ogg Opus
    t0 = time.time()
    so = ort.SessionOptions()
    so.intra_op_num_threads, so.inter_op_num_threads = s["threads"], 1
    so.enable_cpu_mem_arena = False     # the arena keeps its high-water mark; ~170 MB less at peak without it
    model = onnx_asr.load_model("nemo-parakeet-tdt-0.6b-v2", s["parakeet_dir"], quantization="int8", sess_options=so)
    vad = onnx_asr.load_vad("silero", s["vad_dir"], sess_options=so)
    # One speech segment per encoder call. onnx-asr's default puts 8 segments of up to 25 s through at once, padded
    # to the longest: on John's 4 clips (2026-11-10) that peaked at 1.8 to 2.9 GB, and batch 1 with no arena peaked
    # at 1.1 to 1.2 GB, ran 1.6 to 4x faster on 2 threads (43 s clip: 28 s to 6.6 s), and kept as many ums (72 to 69).
    asr = model.with_vad(vad, max_speech_duration_s=25, min_silence_duration_ms=300, batch_size=1).with_timestamps()
    t1 = time.time()
    audio = decode_audio(str(path), sampling_rate=SAMPLE_RATE)
    segs = [(seg.start, seg.tokens or [], seg.timestamps or []) for seg in asr.recognize(audio, sample_rate=SAMPLE_RATE)]
    return rc.token_words(segs), len(audio) / SAMPLE_RATE, t1 - t0, time.time() - t1, "parakeet-tdt-0.6b-v2-int8"


def main(argv):
    path, s = argv[1], json.loads(argv[2])
    s.setdefault("threads", 2)
    which = s.get("transcriber", "whisper")
    run = {"parakeet": parakeet_words, "whisper": whisper_words}[which]
    words, audio_s, load_s, transcribe_s, model = run(path, s)
    print(json.dumps({"text": rc.with_pauses(words, s.get("pause_seconds", 1.5)), "model": model,
                      "audio_s": round(audio_s, 1), "load_s": round(load_s, 1), "transcribe_s": round(transcribe_s, 1),
                      "peak_rss_mb": resource.getrusage(resource.RUSAGE_SELF).ru_maxrss // 1024}), flush=True)


if __name__ == "__main__":
    main(sys.argv)
