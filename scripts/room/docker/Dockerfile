# A room bridge in a Linux container, for a machine without systemd: John's Windows PC runs Ember's (2026-11-10),
# beside Ember's own app container. The code is mounted, not copied (`-v .../scripts/room:/app:ro`), so a pull and
# a restart update it; config, token and state live in a named volume at /root/.config/persistence-room.
FROM python:3.12-slim
COPY requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir -r /tmp/requirements.txt && rm /tmp/requirements.txt
ENV PYTHONUNBUFFERED=1
CMD ["python3", "/app/bridge.py"]
