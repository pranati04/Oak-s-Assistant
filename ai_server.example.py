"""
PokeAI Bridge Server — Google Gemini edition
─────────────────────────────────────────────
Translates requests from the BizHawk C# plugin → Gemini API format,
then translates Gemini responses back → the format the plugin expects.

Run:  python ai_server.py
Requires: pip install flask requests
"""

import json
import time
import requests
from flask import Flask, request, jsonify

# ═══════════════════════════════════════════════════════════════
#  YOUR GEMINI API KEY — PASTE IT RIGHT HERE
#
#  Get it from: https://aistudio.google.com/app/apikey
#  Click "Create API key", copy the whole thing.
#  It looks like: AIzaSy...
#
#  Replace the placeholder below with your real key:
#    API_KEY = "AIzaSyABC123yourrealkeyhere"
#
#  ⚠ Never share this file or push it to GitHub with a real key.
# ═══════════════════════════════════════════════════════════════
API_KEY = "AIzaSy-YOUR_KEY_HERE"

# ── Model config ──────────────────────────────────────────────
# gemini-2.0-flash   — fast, cheap, very capable  ← recommended
# gemini-1.5-pro     — slower, more powerful
# gemini-1.5-flash   — fast alternative
MODEL      = "gemini-flash-latest"     # free tier, generous quota, v1beta supported
MAX_TOKENS = 1024
PORT       = 5000

# Gemini REST endpoint
def gemini_url():
    return (
        f"https://generativelanguage.googleapis.com/v1beta/models/"
        f"{MODEL}:generateContent?key={API_KEY}"
    )

HEADERS = {"Content-Type": "application/json"}

# ── Simple in-memory cache ────────────────────────────────────
_cache: dict[str, tuple[str, float]] = {}
CACHE_TTL = 60  # seconds


app = Flask(__name__)


def build_gemini_payload(system_prompt: str, messages: list) -> dict:
    """
    Convert Anthropic-style messages + system prompt → Gemini format.

    Gemini uses:
    {
      "system_instruction": { "parts": [{"text": "..."}] },
      "contents": [
        {"role": "user",  "parts": [{"text": "..."}]},
        {"role": "model", "parts": [{"text": "..."}]}   ← "assistant" becomes "model"
      ],
      "generationConfig": { "maxOutputTokens": N }
    }
    """
    contents = []
    for msg in messages:
        role = "model" if msg.get("role") == "assistant" else "user"
        contents.append({
            "role": role,
            "parts": [{"text": msg.get("content", "")}]
        })

    payload = {
        "contents": contents,
        "generationConfig": {
            "maxOutputTokens": MAX_TOKENS,
            "temperature": 0.7
        }
    }

    if system_prompt:
        payload["system_instruction"] = {
            "parts": [{"text": system_prompt}]
        }

    return payload


def extract_gemini_text(gemini_resp: dict) -> str:
    """
    Pull the text out of Gemini's response structure:
    { "candidates": [{ "content": { "parts": [{"text": "..."}] } }] }
    """
    try:
        return gemini_resp["candidates"][0]["content"]["parts"][0]["text"]
    except (KeyError, IndexError) as e:
        # Surface the raw response if parsing fails
        raise ValueError(f"Unexpected Gemini response structure: {gemini_resp}") from e


def to_anthropic_format(text: str) -> dict:
    """Wrap text in the Anthropic-style response the C# plugin expects."""
    return {"content": [{"type": "text", "text": text}]}


@app.route("/chat", methods=["POST"])
def chat():
    try:
        body = request.get_json(force=True)
    except Exception as e:
        return jsonify({"error": f"Bad JSON: {e}"}), 400

    if "messages" not in body:
        return jsonify({"error": "Missing messages field"}), 400

    system_prompt = body.get("system", "")
    messages      = body.get("messages", [])

    # Cache single-turn advisor calls
    cache_key = None
    if len(messages) == 1:
        cache_key = str(hash(json.dumps(body, sort_keys=True)))
        if cache_key in _cache:
            cached, ts = _cache[cache_key]
            if time.time() - ts < CACHE_TTL:
                return app.response_class(cached, mimetype="application/json")

    payload = build_gemini_payload(system_prompt, messages)

    models_to_try = [MODEL, "gemini-1.5-flash-8b", "gemini-1.5-flash"]
    
    resp = None
    last_error = "Unknown Error"
    last_detail = "All fallback models failed."
    last_status = 500

    for m in models_to_try:
        url = f"https://generativelanguage.googleapis.com/v1beta/models/{m}:generateContent?key={API_KEY}"
        try:
            resp = requests.post(url, headers=HEADERS, json=payload, timeout=30)
            if resp.status_code in (429, 503, 404):
                last_error = f"API Issue (Code {resp.status_code})"
                last_detail = resp.text
                last_status = resp.status_code
                continue
                
            resp.raise_for_status()
            break # Success, exit retry loop
            
        except requests.Timeout:
            last_error = "Gemini API timeout"
            last_detail = f"Model {m} timed out after 30s"
            last_status = 504
        except requests.HTTPError as e:
            last_error = str(e)
            try:
                last_detail = resp.json().get("error", {}).get("message", resp.text)
            except Exception:
                last_detail = resp.text if resp else str(e)
            last_status = resp.status_code if resp else 500

    if not resp or resp.status_code != 200:
        return jsonify({"error": last_error, "detail": last_detail}), last_status

    try:
        text   = extract_gemini_text(resp.json())
        result = json.dumps(to_anthropic_format(text))
    except ValueError as e:
        return jsonify({"error": str(e)}), 500

    if cache_key:
        _cache[cache_key] = (result, time.time())

    return app.response_class(result, mimetype="application/json")


@app.route("/health", methods=["GET"])
def health():
    return jsonify({"status": "ok", "model": MODEL, "port": PORT, "provider": "Google Gemini"})


@app.route("/clear_cache", methods=["POST"])
def clear_cache():
    _cache.clear()
    return jsonify({"cleared": True})


if __name__ == "__main__":
    key_set    = not API_KEY.endswith("HERE")
    key_status = "✓ set" if key_set else "⚠ NOT SET — paste your key into ai_server.py (line 28)"

    print(f"""
  ██████╗  ██████╗ ██╗  ██╗███████╗ █████╗ ██╗
  ██╔══██╗██╔═══██╗██║ ██╔╝██╔════╝██╔══██╗██║
  ██████╔╝██║   ██║█████╔╝ █████╗  ███████║██║
  ██╔═══╝ ██║   ██║██╔═██╗ ██╔══╝  ██╔══██║██║
  ██║     ╚██████╔╝██║  ██╗███████╗██║  ██║██║
  ╚═╝      ╚═════╝ ╚═╝  ╚═╝╚══════╝╚═╝  ╚═╝╚═╝

  PokeAI Bridge Server
  Provider : Google Gemini
  Model    : {MODEL}
  Port     : {PORT}
  API key  : {key_status}
    """)

    if not key_set:
        print("  → Open ai_server.py and replace AIzaSy-YOUR_KEY_HERE with your real key.\n")
    else:
        # Quick check — list available models so we know what works with this key
        try:
            r = requests.get(
                f"https://generativelanguage.googleapis.com/v1beta/models?key={API_KEY}",
                timeout=8
            )
            if r.ok:
                names = [m["name"].split("/")[-1] for m in r.json().get("models", [])]
                flash = [n for n in names if "flash" in n.lower()]
                print(f"  Available flash models: {', '.join(flash[:6]) if flash else 'none found'}")
                if MODEL not in names:
                    print(f"  ⚠ '{MODEL}' not in your model list — using first available flash model")
                    if flash:
                        import sys
                        # Patch the module-level MODEL for this run
                        import ai_server
                        ai_server.MODEL = flash[0]
                        print(f"  → Switched to: {flash[0]}")
            else:
                print(f"  Could not list models: {r.status_code}")
        except Exception as e:
            print(f"  Could not list models: {e}")

    app.run(host="127.0.0.1", port=PORT, debug=False)