// Talk directly to ASP.NET Core backend to bypass any dev server proxy buffering
const API_BASE = 'http://localhost:61622';

// Case-insensitive helpers to prevent property casing mismatches
export function getProp(obj, key) {
  if (!obj) return undefined;
  const lowerKey = key.toLowerCase();
  for (const k of Object.keys(obj)) {
    if (k.toLowerCase() === lowerKey) {
      return obj[k];
    }
  }
  return undefined;
}

export async function fetchConversations() {
  const res = await fetch(`${API_BASE}/api/conversations`);
  if (!res.ok) throw new Error(`Failed to load sessions: ${res.statusText}`);
  return await res.json();
}

export async function fetchDocuments() {
  const res = await fetch(`${API_BASE}/api/documents`);
  if (!res.ok) throw new Error(`Failed to load files status: ${res.statusText}`);
  return await res.json();
}

export async function createConversation(name) {
  const res = await fetch(`${API_BASE}/api/conversations/new`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name })
  });
  if (!res.ok) throw new Error(`Failed to initialize session: ${res.statusText}`);
  return await res.json();
}

export async function fetchConversationDetails(id) {
  const res = await fetch(`${API_BASE}/api/conversations/${id}`);
  if (!res.ok) throw new Error(`Failed to fetch history details: ${res.statusText}`);
  return await res.json();
}

export async function uploadFile(file) {
  const formData = new FormData();
  formData.append('file', file); // Maps directly to IFormFile file parameter in ASP.NET Core

  const res = await fetch(`${API_BASE}/api/upload`, {
    method: 'POST',
    body: formData // Automatically configures multipart/form-data with bounds
  });

  if (!res.ok) {
    const errText = await res.text();
    throw new Error(errText || `Indexing failed: ${res.statusText}`);
  }

  return await res.json();
}

export async function renameConversation(id, name) {
  const res = await fetch(`${API_BASE}/api/conversations/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name })
  });
  if (!res.ok) throw new Error(`Failed to rename conversation: ${res.statusText}`);
  return await res.json();
}

export async function deleteConversation(id) {
  const res = await fetch(`${API_BASE}/api/conversations/${id}`, {
    method: 'DELETE'
  });
  if (!res.ok) throw new Error(`Failed to delete conversation: ${res.statusText}`);
  return await res.json();
}

export async function sendChatMessage(conversationId, message, documentId) {
  const res = await fetch(`${API_BASE}/api/chat`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      conversationId,
      message,
      documentIds: documentId ? [documentId] : []
    })
  });

  if (!res.ok) {
    const errJson = await res.json().catch(() => null);
    throw new Error(errJson?.error || `Agent query failed: ${res.statusText}`);
  }

  return await res.json();
}

export async function* sendChatMessageStream(conversationId, message, documentId) {
  const res = await fetch(`${API_BASE}/api/chat/stream`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      conversationId,
      message,
      documentIds: documentId ? [documentId] : []
    })
  });

  if (!res.ok) {
    const errJson = await res.json().catch(() => null);
    throw new Error(errJson?.error || `Agent streaming query failed: ${res.statusText}`);
  }

  const reader = res.body.getReader();
  const decoder = new TextDecoder('utf-8');
  let buffer = '';

  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;

      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split('\n');

      // Keep the last partial line in the buffer
      buffer = lines.pop() || '';

      for (const line of lines) {
        const trimmed = line.trim();
        if (!trimmed) continue;

        if (trimmed.startsWith('data:')) {
          const dataStr = trimmed.slice(5).trim();
          if (dataStr === '[DONE]') {
            return;
          }
          try {
            const parsed = JSON.parse(dataStr);
            if (parsed.text) {
              yield parsed.text;
            }
          } catch (e) {
            console.error('Error parsing SSE data line:', e, dataStr);
          }
        }
      }
    }

    // Process any remaining data in the buffer
    if (buffer) {
      const trimmed = buffer.trim();
      if (trimmed.startsWith('data:')) {
        const dataStr = trimmed.slice(5).trim();
        if (dataStr !== '[DONE]') {
          try {
            const parsed = JSON.parse(dataStr);
            if (parsed.text) {
              yield parsed.text;
            }
          } catch (e) {
            console.error('Error parsing trailing SSE data:', e, dataStr);
          }
        }
      }
    }
  } finally {
    reader.releaseLock();
  }
}
