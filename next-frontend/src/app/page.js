"use client";

import { useState, useEffect, useRef } from 'react';
import {
  getProp,
  fetchConversations,
  fetchDocuments,
  createConversation,
  fetchConversationDetails,
  uploadFile,
  renameConversation,
  deleteConversation,
  sendChatMessageStream
} from './api';

export default function Home() {
  // --- STATE HOOKS ---
  const [conversations, setConversations] = useState([]);
  const [isDarkMode, setIsDarkMode] = useState(false);

  // --- THEME SYNC ---
  useEffect(() => {
    const savedTheme = localStorage.getItem('theme');
    if (savedTheme === 'dark') {
      setIsDarkMode(true);
    } else {
      setIsDarkMode(false);
    }
  }, []);

  useEffect(() => {
    const html = document.documentElement;
    if (isDarkMode) {
      html.classList.remove('light-theme');
      html.classList.add('dark-theme');
    } else {
      html.classList.remove('dark-theme');
      html.classList.add('light-theme');
    }
  }, [isDarkMode]);
  const [documents, setDocuments] = useState([]);
  const [activeConversationId, setActiveConversationId] = useState(null);
  const [activeConvoDetail, setActiveConvoDetail] = useState(null);
  const [chatInputText, setChatInputText] = useState('');
  const [chatDocFilter, setChatDocFilter] = useState('');
  const [isSending, setIsSending] = useState(false);
  const [errorMessage, setErrorMessage] = useState(null);
  const [temporaryDocs, setTemporaryDocs] = useState([]);
  const [isDragOver, setIsDragOver] = useState(false);
  const [expandedCitations, setExpandedCitations] = useState({}); // messageIndex -> boolean
  const [streamingText, setStreamingText] = useState('');

  // --- MANUAL SIDEBAR ACTIONS STATE ---
  const [editingConversationId, setEditingConversationId] = useState(null);
  const [renameInputVal, setRenameInputVal] = useState('');
  const [conversationsOpen, setConversationsOpen] = useState(true);
  const [docsOpen, setDocsOpen] = useState(true);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);

  // --- REFS ---
  const chatMessagesEndRef = useRef(null);
  const fileInputRef = useRef(null);

  // --- INITIALIZATION ---
  useEffect(() => {
    async function init() {
      try {
        const convos = await fetchConversations();
        setConversations(convos);

        const docs = await fetchDocuments();
        setDocuments(docs);

        // Auto-select first or create a new one
        if (convos && convos.length > 0) {
          const firstId = getProp(convos[0], 'id');
          setActiveConversationId(firstId);
        } else {
          await handleCreateNewChat();
        }
      } catch (err) {
        console.error('Initialization error:', err);
        setErrorMessage(`System offline: ${err.message}`);
      }
    }
    init();
  }, []);

  // --- CONVERSATION SELECTION ---
  useEffect(() => {
    if (!activeConversationId) return;

    async function loadActiveDetails() {
      try {
        const details = await fetchConversationDetails(activeConversationId);
        setActiveConvoDetail(details);
      } catch (err) {
        console.error('Failed to load conversation details:', err);
        setErrorMessage(`Could not load active chat: ${err.message}`);
      }
    }

    loadActiveDetails();
  }, [activeConversationId]);

  // --- BACKGROUND POLLING FOR INGESTION STATE ---
  useEffect(() => {
    const hasPendingOrProcessing = documents.some(doc => {
      const status = getProp(doc, 'status');
      return status === 'Pending' || status === 'Processing';
    });

    const hasTempDocs = temporaryDocs.length > 0;

    if (!hasPendingOrProcessing && !hasTempDocs) return;

    const interval = setInterval(async () => {
      try {
        console.log('[Polling] Reviewing ingestion pipeline state...');
        const updatedDocs = await fetchDocuments();
        setDocuments(updatedDocs);

        // Clear temporary items if their actual counterparts are now in the fetched documents
        setTemporaryDocs(prev => 
          prev.filter(temp => !updatedDocs.some(d => getProp(d, 'fileName') === temp.name))
        );
      } catch (err) {
        console.error('Polling error:', err);
      }
    }, 5000);

    return () => clearInterval(interval);
  }, [documents, temporaryDocs]);

  // --- SCROLL TO BOTTOM ON NEW MESSAGES ---
  useEffect(() => {
    chatMessagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [activeConvoDetail, isSending]);

  // --- EVENT HANDLERS ---

  const handleCreateNewChat = async () => {
    try {
      const newSession = await createConversation(`New Chat`);
      
      setConversations(prev => [newSession, ...prev]);
      setActiveConversationId(getProp(newSession, 'id'));
    } catch (err) {
      console.error(err);
      setErrorMessage(`Failed to start session: ${err.message}`);
    }
  };

  const handleFileSelectionChange = async (e) => {
    if (e.target.files && e.target.files.length > 0) {
      await handleUploadFiles(e.target.files);
    }
    e.target.value = '';
  };

  const handleUploadFiles = async (filesList) => {
    const files = Array.from(filesList);
    const mockTempItems = files.map(f => ({ name: f.name, id: Math.random().toString() }));
    setTemporaryDocs(prev => [...mockTempItems, ...prev]);

    for (let file of files) {
      try {
        await uploadFile(file);
        const updatedDocs = await fetchDocuments();
        setDocuments(updatedDocs);
      } catch (err) {
        console.error(`File upload error for ${file.name}:`, err);
        setErrorMessage(`Ingestion failed for "${file.name}": ${err.message}`);
      } finally {
        setTemporaryDocs(prev => prev.filter(t => t.name !== file.name));
      }
    }
  };

  const handleSendMessage = async () => {
    const text = chatInputText.trim();
    if (!text || isSending || !activeConversationId) return;

    setIsSending(true);
    setChatInputText('');
    setStreamingText('');

    // Append mock user message instantly for UI responsiveness
    const mockUserMsg = {
      sender: 'user',
      text: text,
      timestamp: new Date().toISOString()
    };

    setActiveConvoDetail(prev => {
      if (!prev) return null;
      const msgs = getProp(prev, 'messages') || [];
      return {
        ...prev,
        messages: [...msgs, mockUserMsg]
      };
    });

    let accumulatedResponseText = '';

    try {
      const stream = sendChatMessageStream(activeConversationId, text, chatDocFilter);
      
      for await (const chunk of stream) {
        accumulatedResponseText += chunk;
        setStreamingText(accumulatedResponseText);
      }

      // Re-fetch conversation details from SQL database to load citations & finalized messages
      const finalDetails = await fetchConversationDetails(activeConversationId);
      setActiveConvoDetail(finalDetails);

      // Refresh conversation sessions list to catch auto-titled session names!
      const convos = await fetchConversations();
      setConversations(convos);

    } catch (err) {
      console.error('Streaming chat failed:', err);
      setErrorMessage(`Streaming error: ${err.message}`);

      // Save user & error assistant message locally for display
      const errMsgText = accumulatedResponseText 
        ? accumulatedResponseText + '\n\n⚠️ Streaming connection interrupted. Please ensure the backend is running.'
        : '⚠️ Failed to connect to the backend server. Please make sure the service is running.';

      setActiveConvoDetail(prev => {
        if (!prev) return null;
        const msgs = [...(getProp(prev, 'messages') || [])];
        return {
          ...prev,
          messages: [
            ...msgs,
            {
              sender: 'assistant',
              text: errMsgText,
              timestamp: new Date().toISOString()
            }
          ]
        };
      });
    } finally {
      setIsSending(false);
      setStreamingText('');
    }
  };

  // --- SIDEBAR MANUAL ACTION HANDLERS ---
  const startRenameConversation = (convoId, currentName, e) => {
    e.stopPropagation();
    setEditingConversationId(convoId);
    setRenameInputVal(currentName);
  };

  const submitRenameConversation = async (convoId) => {
    const targetName = renameInputVal.trim();
    if (!targetName) {
      setEditingConversationId(null);
      return;
    }

    try {
      await renameConversation(convoId, targetName);
      const convos = await fetchConversations();
      setConversations(convos);

      if (activeConversationId === convoId) {
        setActiveConvoDetail(prev => prev ? { ...prev, name: targetName } : null);
      }
    } catch (err) {
      console.error('Rename failed:', err);
      setErrorMessage(`Rename failed: ${err.message}`);
    } finally {
      setEditingConversationId(null);
    }
  };

  const handleDeleteConversation = async (convoId, e) => {
    e.stopPropagation();
    if (!confirm('Are you sure you want to delete this conversation?')) return;

    try {
      await deleteConversation(convoId);
      const convos = await fetchConversations();
      setConversations(convos);

      if (activeConversationId === convoId) {
        if (convos && convos.length > 0) {
          setActiveConversationId(getProp(convos[0], 'id'));
        } else {
          await handleCreateNewChat();
        }
      }
    } catch (err) {
      console.error('Delete failed:', err);
      setErrorMessage(`Delete failed: ${err.message}`);
    }
  };

  // --- RENDER UTILITIES ---
  const getFileIconClass = (fileName) => {
    if (!fileName) return 'fa-regular fa-file';
    const ext = fileName.split('.').pop().toLowerCase();
    switch (ext) {
      case 'pdf': return 'fa-regular fa-file-pdf text-danger';
      case 'docx': return 'fa-regular fa-file-word text-primary';
      case 'csv': return 'fa-solid fa-file-csv text-success';
      case 'json': return 'fa-regular fa-file-code text-warning';
      case 'md': return 'fa-regular fa-file-lines text-info';
      default: return 'fa-regular fa-file';
    }
  };

  const getStatusHtml = (status) => {
    switch (status) {
      case 'Pending':
        return (
          <>
            <span className="pulse-dot status-pending"></span> Pending Indexing
          </>
        );
      case 'Processing':
        return (
          <>
            <i className="fa-solid fa-circle-notch spinner-icon status-processing"></i> Ingesting...
          </>
        );
      case 'Indexed':
        return (
          <>
            <i className="fa-regular fa-circle-check status-indexed"></i> Loaded
          </>
        );
      case 'Failed':
        return (
          <>
            <i className="fa-regular fa-circle-xmark status-failed"></i> Ingestion Failed
          </>
        );
      default:
        return status;
    }
  };

  const formatMessageText = (text) => {
    if (!text) return '';

    let formatted = text
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;');

    // Format headers & dividers
    formatted = formatted.replace(/^\s*####\s+(.*)$/gm, '<h4>$1</h4>');
    formatted = formatted.replace(/^\s*###\s+(.*)$/gm, '<h3>$1</h3>');
    formatted = formatted.replace(/^\s*##\s+(.*)$/gm, '<h2>$1</h2>');
    formatted = formatted.replace(/^\s*---\s*$/gm, '<hr class="msg-divider" />');

    // Parse Tables
    const lines = formatted.split('\n');
    const processedLines = [];
    let inTable = false;
    let tableRows = [];

    const renderTableHtml = (rows) => {
      if (rows.length < 2) return rows.join('\n');
      
      const parseRow = (rowStr) => {
        const cells = rowStr.split('|').map(c => c.trim());
        if (cells[0] === '') cells.shift();
        if (cells[cells.length - 1] === '') cells.pop();
        return cells;
      };

      const headerCells = parseRow(rows[0]);
      const separatorRow = rows[1];
      const isSeparator = /^[|:\-\s]+$/.test(separatorRow);
      
      if (!isSeparator) return rows.join('\n');

      let html = '<div class="table-container"><table class="premium-table">';
      html += '<thead><tr>';
      for (let cell of headerCells) {
        let formattedCell = cell.replace(/\*\*(.*?)\*\*/g, '<strong>$1</strong>');
        html += `<th>${formattedCell}</th>`;
      }
      html += '</tr></thead>';

      html += '<tbody>';
      for (let i = 2; i < rows.length; i++) {
        const cells = parseRow(rows[i]);
        html += '<tr>';
        for (let j = 0; j < headerCells.length; j++) {
          const cell = cells[j] || '';
          let formattedCell = cell
            .replace(/\*\*(.*?)\*\*/g, '<strong>$1</strong>')
            .replace(/&lt;br\s*\/?&gt;/gi, '<br>') 
            .replace(/&amp;nbsp;/g, '&nbsp;');
          html += `<td>${formattedCell}</td>`;
        }
        html += '</tr>';
      }
      html += '</tbody></table></div>';
      return html;
    };

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i].trim();
      const isTableRow = line.startsWith('|') && line.endsWith('|') && line.length > 1;

      if (isTableRow) {
        if (!inTable) {
          inTable = true;
          tableRows = [];
        }
        tableRows.push(line);
      } else {
        if (inTable) {
          processedLines.push(renderTableHtml(tableRows));
          inTable = false;
          tableRows = [];
        }
        processedLines.push(lines[i]);
      }
    }
    
    if (inTable && tableRows.length > 0) {
      processedLines.push(renderTableHtml(tableRows));
    }

    formatted = processedLines.join('\n');

    // Format code blocks
    formatted = formatted.replace(/```([\s\S]*?)```/g, '<pre><code>$1</code></pre>');
    
    // Format bullet points
    formatted = formatted.replace(/^\s*-\s+(.*)$/gm, '<li>$1</li>');
    formatted = formatted.replace(/(<li>.*<\/li>)/g, '<ul>$1</ul>');
    formatted = formatted.replace(/<\/ul>\s*<ul>/g, ''); 

    // Format bold text
    formatted = formatted.replace(/\*\*(.*?)\*\*/g, '<strong>$1</strong>');

    // Replace newlines safely
    const blocks = [];
    formatted = formatted.replace(/(<pre[\s\S]*?<\/pre>|<div class="table-container"[\s\S]*?<\/div>)/g, (match) => {
      const placeholder = `___BLOCK_PLACEHOLDER_${blocks.length}___`;
      blocks.push(match);
      return placeholder;
    });

    formatted = formatted.replace(/\n\n/g, '<br><br>');
    formatted = formatted.replace(/\n/g, '<br>');

    for (let i = 0; i < blocks.length; i++) {
      formatted = formatted.replace(`___BLOCK_PLACEHOLDER_${i}___`, blocks[i]);
    }

    // Clean up redundant break tags adjacent to block-level elements to prevent compounding spaces (Run AFTER restoring placeholders!)
    formatted = formatted.replace(/(<(?:h[234]|hr|ul|ol|li|pre|table|thead|tbody|tr|div)[^>]*>)\s*(?:<br\s*\/?>)+/gi, '$1');
    formatted = formatted.replace(/(?:<br\s*\/?>)+\s*(<\/(?:h[234]|hr|ul|ol|li|pre|table|thead|tbody|tr|div)>)/gi, '$1');
    formatted = formatted.replace(/(?:<br\s*\/?>)+\s*(<(?:h[234]|hr|ul|ol|li|pre|table|thead|tbody|tr|div)[^>]*>)/gi, '$1');

    return formatted;
  };

  const indexedDocs = documents.filter(d => getProp(d, 'status') === 'Indexed');

  return (
    <div className="app-container">
      {/* FLOATING ERROR ALERT BANNER */}
      {errorMessage && (
        <div className="floating-error-banner">
          <span><i className="fa-solid fa-triangle-exclamation"></i> {errorMessage}</span>
          <button onClick={() => setErrorMessage(null)} aria-label="Dismiss error">×</button>
        </div>
      )}

      {/* SIDEBAR */}
      <aside className={`sidebar${sidebarCollapsed ? ' collapsed' : ''}`}>
        <div className="sidebar-header">
          <div className="brand">
            <div className="brand-info">
              <h2>RAG Pipeline Demo</h2>
              <span>MS Agent Framework</span>
            </div>
          </div>
          <button
            type="button"
            className="sidebar-collapse-btn"
            onClick={() => setSidebarCollapsed(prev => !prev)}
            aria-label={sidebarCollapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          >
            <i className={`fa-solid fa-chevron-${sidebarCollapsed ? 'right' : 'left'}`}></i>
          </button>
        </div>

        {/* Action Button */}
        <button onClick={handleCreateNewChat} className="btn-new-chat">
          <i className="fa-solid fa-plus"></i> New Conversation
        </button>

        {/* Conversations List */}
        <div className="sidebar-section">
          <div className="section-title">
            <div className="section-title-left">
              <span>Conversations</span>
              <i className="fa-regular fa-message"></i>
            </div>
            <button
              type="button"
              className="section-toggle"
              onClick={() => setConversationsOpen(prev => !prev)}
              aria-expanded={conversationsOpen}
              aria-label="Toggle Conversations"
            >
              <i className={`fa-solid fa-chevron-${conversationsOpen ? 'down' : 'right'}`}></i>
            </button>
          </div>
          {conversationsOpen && (
            <div className="conversations-list">
            {conversations.length === 0 ? (
              <div className="list-placeholder">No conversations yet</div>
            ) : (
              conversations.map(convo => {
                const convoId = getProp(convo, 'id');
                const convoName = getProp(convo, 'name');
                const isActive = convoId === activeConversationId;
                const isEditing = convoId === editingConversationId;

                return (
                  <div 
                    key={convoId} 
                    className={`conversation-item-wrapper ${isActive ? 'active' : ''}`}
                  >
                    {isEditing ? (
                      <input
                        type="text"
                        className="rename-input"
                        value={renameInputVal}
                        onChange={(e) => setRenameInputVal(e.target.value)}
                        onBlur={() => submitRenameConversation(convoId)}
                        onKeyDown={(e) => {
                          if (e.key === 'Enter') submitRenameConversation(convoId);
                          if (e.key === 'Escape') setEditingConversationId(null);
                        }}
                        autoFocus
                      />
                    ) : (
                      <>
                        <button
                          onClick={() => setActiveConversationId(convoId)}
                          onDoubleClick={(e) => startRenameConversation(convoId, convoName, e)}
                          className="conversation-item"
                          type="button"
                        >
                          <i className="fa-regular fa-comment"></i>
                          <div className="conversation-name">{convoName}</div>
                        </button>
                        
                        <div className="conversation-actions">
                          <button
                            onClick={(e) => startRenameConversation(convoId, convoName, e)}
                            className="action-btn rename-btn"
                            title="Rename Chat"
                            aria-label="Rename Chat"
                          >
                            <i className="fa-regular fa-pen-to-square"></i>
                          </button>
                          <button
                            onClick={(e) => handleDeleteConversation(convoId, e)}
                            className="action-btn delete-btn"
                            title="Delete Chat"
                            aria-label="Delete Chat"
                          >
                            <i className="fa-regular fa-trash-can"></i>
                          </button>
                        </div>
                      </>
                    )}
                  </div>
                );
              })
            )}
          </div>
          )}
        </div>

        {/* Documents Panel */}
        <div className="sidebar-section docs-section">
          <div className="section-title">
            <div className="section-title-left">
              <span>Knowledge Base</span>
              <i className="fa-regular fa-folder-open"></i>
            </div>
            <button
              type="button"
              className="section-toggle"
              onClick={() => setDocsOpen(prev => !prev)}
              aria-expanded={docsOpen}
              aria-label="Toggle Knowledge Base"
            >
              <i className={`fa-solid fa-chevron-${docsOpen ? 'down' : 'right'}`}></i>
            </button>
          </div>
          {docsOpen && (
          <>
          <div
            className={`dropzone ${isDragOver ? 'dragover' : ''}`}
            onClick={() => fileInputRef.current?.click()}
            onDragEnter={(e) => { e.preventDefault(); setIsDragOver(true); }}
            onDragOver={(e) => { e.preventDefault(); setIsDragOver(true); }}
            onDragLeave={(e) => { e.preventDefault(); setIsDragOver(false); }}
            onDrop={async (e) => {
              e.preventDefault();
              setIsDragOver(false);
              if (e.dataTransfer.files && e.dataTransfer.files.length > 0) {
                await handleUploadFiles(e.dataTransfer.files);
              }
            }}
          >
            <input
              type="file"
              ref={fileInputRef}
              onChange={handleFileSelectionChange}
              onClick={(e) => e.stopPropagation()}
              className="file-input"
              multiple
              accept=".pdf,.docx"
            />
            <i className="fa-solid fa-cloud-arrow-up drop-icon"></i>
            <p className="drop-text">Drag & drop files or <span>browse</span></p>
            <p className="drop-subtext">PDF or DOCX contracts</p>
          </div>

          <div className="documents-list">
            {temporaryDocs.map(temp => (
              <div key={temp.id} className="document-item temporary">
                <div className="doc-header">
                  <div className="doc-title-container">
                    <i className={`${getFileIconClass(temp.name)} doc-icon`}></i>
                    <span className="doc-name" title={temp.name}>{temp.name}</span>
                  </div>
                  <div className="doc-status">
                    <span className="pulse-dot status-pending"></span> Uploading...
                  </div>
                </div>
              </div>
            ))}

            {documents.length === 0 && temporaryDocs.length === 0 ? (
              <div className="list-placeholder">No files indexed yet</div>
            ) : (
              documents.map(doc => {
                const docId = getProp(doc, 'id');
                const docFileName = getProp(doc, 'fileName');
                const docStatus = getProp(doc, 'status');
                const agreementType = getProp(doc, 'agreementType');
                const partyA = getProp(doc, 'partyA');
                const partyB = getProp(doc, 'partyB');
                const effectiveDate = getProp(doc, 'effectiveDate');
                const isLatestVal = getProp(doc, 'isLatest');
                const versionNum = getProp(doc, 'version');

                const hasLatestVal = isLatestVal !== undefined && isLatestVal !== null;
                const isActive = hasLatestVal ? (isLatestVal === true || String(isLatestVal).toLowerCase() === 'true') : true;
                const version = versionNum !== undefined && versionNum !== null ? `v${versionNum}` : '';
                const partyText = [partyA, partyB]
                  .filter(p => p && String(p).toLowerCase() !== 'unknown')
                  .join(' / ');

                return (
                  <div key={docId} className="document-item">
                    <div className="doc-header">
                      <div className="doc-title-container">
                        <i className={`${getFileIconClass(docFileName)} doc-icon`}></i>
                        <span className="doc-name" title={docFileName}>{docFileName}</span>
                      </div>
                      <div className="doc-status">{getStatusHtml(docStatus)}</div>
                    </div>
                    
                    <div className="doc-meta-row">
                      <span className={`badge ${isActive ? 'badge-active' : 'badge-archived'}`}>
                        {isActive ? 'Active' : 'Archived'}
                      </span>
                      {agreementType && (
                        <span className="badge badge-type" title={`Agreement Type: ${agreementType}`}>
                          {agreementType}
                        </span>
                      )}
                      {version && (
                        <span className="badge badge-version" title={`Version: ${version}`}>
                          {version}
                        </span>
                      )}
                    </div>

                    {partyText && (
                      <div className="doc-fiscal-row">
                        <i className="fa-regular fa-handshake"></i>
                        <span>{partyText}</span>
                      </div>
                    )}

                    {effectiveDate && String(effectiveDate).toLowerCase() !== 'unknown' && (
                      <div className="doc-fiscal-row">
                        <i className="fa-regular fa-calendar-days"></i>
                        <span>Effective {effectiveDate}</span>
                      </div>
                    )}
                  </div>
                );
              })
            )}
          </div>
          </>
          )}
        </div>
      </aside>

      {/* MAIN WORKSPACE */}
      <main className="chat-workspace">
        <header className="chat-header">
          <div className="active-convo-info">
            <div className="convo-avatar">
              <i className="fa-solid fa-comments"></i>
            </div>
            <div>
              <h1>{activeConvoDetail ? getProp(activeConvoDetail, 'name') : 'Select a Conversation'}</h1>
              <p className={activeConvoDetail ? 'status-online' : 'status-offline'}>
                <span className="status-dot"></span> {activeConvoDetail ? 'Agent Ready' : 'Offline'}
              </p>
            </div>
          </div>
          <div className="header-actions">
            <button
              onClick={() => {
                setIsDarkMode(prev => {
                  const next = !prev;
                  localStorage.setItem('theme', next ? 'dark' : 'light');
                  return next;
                });
              }}
              className="theme-toggle-btn"
              title={isDarkMode ? "Switch to Light Mode" : "Switch to Dark Mode"}
              aria-label="Toggle Theme"
              type="button"
            >
              {isDarkMode ? (
                <i className="fa-solid fa-sun text-warning-icon"></i>
              ) : (
                <i className="fa-solid fa-moon text-dark-icon"></i>
              )}
            </button>

            <div className="filter-dropdown-container">
              <i className="fa-solid fa-filter dropdown-icon"></i>
              <select
                value={chatDocFilter}
                onChange={(e) => setChatDocFilter(e.target.value)}
                className="chat-doc-filter"
              >
                <option value="">Search across all files</option>
                {indexedDocs.map(doc => {
                  const docId = getProp(doc, 'id');
                  const docFileName = getProp(doc, 'fileName');
                  const isLatestVal = getProp(doc, 'isLatest');
                  const hasLatestVal = isLatestVal !== undefined && isLatestVal !== null;
                  const isActive = hasLatestVal ? (isLatestVal === true || String(isLatestVal).toLowerCase() === 'true') : true;
                  const label = isActive ? docFileName : `${docFileName} (Archived)`;

                  return (
                    <option key={docId} value={docId}>
                      {label}
                    </option>
                  );
                })}
              </select>
            </div>
          </div>
        </header>
        {/* Message History */}
        <section className="chat-messages">
          {!activeConvoDetail || (getProp(activeConvoDetail, 'messages') || []).length === 0 ? (
            <div className="welcome-screen">
              <div className="welcome-logo">
                <i className="fa-solid fa-robot"></i>
              </div>
              <h2>Ask Contract RAG Assistant</h2>
              <p>Upload contract documents and ask source-backed questions about clauses, parties, dates, obligations, and comparisons.</p>
              
              <div className="welcome-steps">
                <div className="step-card">
                  <span className="step-num">1</span>
                  <h3>Upload Contracts</h3>
                  <p>Upload PDF or DOCX agreements to parse and index into Pinecone vectors.</p>
                </div>
                <div className="step-card">
                  <span className="step-num">2</span>
                  <h3>Pick Context</h3>
                  <p>Target a specific file using the filter dropdown, or chat across your entire collection.</p>
                </div>
                <div className="step-card">
                  <span className="step-num">3</span>
                  <h3>Real-time Stream</h3>
                  <p>Answers are generated word-by-word instantly. Smart AI auto-titles conversations on your first query.</p>
                </div>
              </div>
            </div>
          ) : (
            (getProp(activeConvoDetail, 'messages') || []).map((msg, index) => {
              const sender = getProp(msg, 'sender');
              const text = getProp(msg, 'text');
              const timestamp = getProp(msg, 'timestamp');
              const citations = getProp(msg, 'citations') || [];

              return (
                <div key={index} className={`message ${sender === 'user' ? 'user' : 'assistant'}`}>
                  <div className="msg-avatar">
                    {sender === 'user' ? (
                      <i className="fa-regular fa-user"></i>
                    ) : (
                      <i className="fa-solid fa-robot"></i>
                    )}
                  </div>
                  <div className="msg-content-wrapper">
                    <div
                      className="msg-bubble"
                      dangerouslySetInnerHTML={{ __html: formatMessageText(text) }}
                    />

                    {sender === 'assistant' && citations.length > 0 && (
                      <div className={`citations-box ${expandedCitations[index] ? 'open' : ''}`}>
                        <div
                          className="citations-toggle"
                          onClick={() => setExpandedCitations(prev => ({ ...prev, [index]: !prev[index] }))}
                        >
                          <span>
                            <i className="fa-solid fa-magnifying-glass-chart"></i> Referenced Pinecone Vector Context ({citations.length})
                          </span>
                          <i className="fa-solid fa-chevron-down"></i>
                        </div>
                        <div className="citations-content">
                          {citations.map((cit, idx) => {
                            const sourceName = getProp(cit, 'sourceName');
                            const citText = getProp(cit, 'text');
                            return (
                              <div key={idx} className="citation-item">
                                <div className="citation-header">
                                  <span>[Source #{idx + 1}]</span>
                                  <span className="citation-source">{sourceName}</span>
                                </div>
                                <div className="citation-snippet">{citText}</div>
                              </div>
                            );
                          })}
                        </div>
                      </div>
                    )}

                    <span className="msg-time">
                      {timestamp ? new Date(timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : ''}
                    </span>
                  </div>
                </div>
              );
            })
          )}

          {/* Typing bubble indicator (Rendered ONLY when sending and streamingText is empty) */}
          {isSending && !streamingText && (
            <div className="message assistant">
              <div className="msg-avatar">
                <i className="fa-solid fa-robot"></i>
              </div>
              <div className="msg-content-wrapper">
                <div className="msg-bubble typing-bubble">
                  <div className="typing-dot"></div>
                  <div className="typing-dot"></div>
                  <div className="typing-dot"></div>
                </div>
              </div>
            </div>
          )}

          {/* Streaming assistant message bubble (Rendered ONLY when actively streaming text) */}
          {isSending && streamingText && (
            <div className="message assistant">
              <div className="msg-avatar">
                <i className="fa-solid fa-robot"></i>
              </div>
              <div className="msg-content-wrapper">
                <div
                  className="msg-bubble"
                  dangerouslySetInnerHTML={{ __html: formatMessageText(streamingText) }}
                />
                <span className="msg-time">
                  {new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                </span>
              </div>
            </div>
          )}

          <div ref={chatMessagesEndRef} />
        </section>

        {/* Chat Inputs */}
        <footer className="chat-footer">
          <div className="chat-input-wrapper">
            <textarea
              id="chat-input"
              value={chatInputText}
              onChange={(e) => setChatInputText(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && !e.shiftKey) {
                  e.preventDefault();
                  handleSendMessage();
                }
              }}
              placeholder={activeConvoDetail ? "Ask a question about your files..." : "Please select or create a session"}
              rows={1}
              disabled={!activeConvoDetail || isSending}
              style={{
                height: 'auto'
              }}
            />
            <button
              onClick={handleSendMessage}
              className="btn-send"
              disabled={!activeConvoDetail || !chatInputText.trim() || isSending}
              type="button"
            >
              <i className="fa-solid fa-paper-plane"></i>
            </button>
          </div>
          <p className="footer-note">Powered by OpenAI Embeddings (text-embedding-3-small) & Microsoft Agent Framework (Streaming Mode)</p>
        </footer>
      </main>
    </div>
  );
}
