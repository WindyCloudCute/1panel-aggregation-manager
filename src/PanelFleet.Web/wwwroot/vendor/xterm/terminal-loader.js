// PanelFleet 终端前端（本地 xterm.js，无 CDN 依赖）。
// 通过 ES Module 动态加载 xterm.js / addon-fit；加载失败时退化为零依赖「简易终端」。
// 本文件只负责 UI 与浏览器 ⇄ PanelFleet 的帧收发，不包含任何鉴权逻辑（cookie 自动携带）。

const XTERM_BASE = '/vendor/xterm';
let xtermLoadPromise = null;

function ensureStylesheet(href) {
    if (document.querySelector(`link[data-pf-xterm="${href}"]`)) {
        return;
    }
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = href;
    link.dataset.pfXterm = href;
    document.head.appendChild(link);
}

function loadScript(src) {
    return new Promise((resolve, reject) => {
        const existing = document.querySelector(`script[data-pf-src="${src}"]`);
        if (existing) {
            if (existing.dataset.loaded === 'true') {
                resolve();
            } else {
                existing.addEventListener('load', () => resolve());
                existing.addEventListener('error', () => reject(new Error('无法加载 ' + src)));
            }
            return;
        }
        const script = document.createElement('script');
        script.src = src;
        script.async = false;
        script.dataset.pfSrc = src;
        script.onload = () => {
            script.dataset.loaded = 'true';
            resolve();
        };
        script.onerror = () => reject(new Error('无法加载 ' + src));
        document.head.appendChild(script);
    });
}

async function loadXterm() {
    if (window.Terminal) {
        return { Terminal: window.Terminal, FitAddon: window.FitAddon ? window.FitAddon.FitAddon : null };
    }
    if (!xtermLoadPromise) {
        xtermLoadPromise = (async () => {
            ensureStylesheet(XTERM_BASE + '/xterm.css');
            await loadScript(XTERM_BASE + '/xterm.js');
            try {
                await loadScript(XTERM_BASE + '/addon-fit.js');
            } catch {
                // addon-fit 可选：缺失时退化为固定尺寸，不影响终端使用。
            }
            if (!window.Terminal) {
                throw new Error('xterm.js 未正确加载');
            }
            return { Terminal: window.Terminal, FitAddon: window.FitAddon ? window.FitAddon.FitAddon : null };
        })().catch((error) => {
            xtermLoadPromise = null;
            throw error;
        });
    }
    return xtermLoadPromise;
}

function base64Encode(bytes) {
    let binary = '';
    for (let i = 0; i < bytes.length; i++) {
        binary += String.fromCharCode(bytes[i]);
    }
    return btoa(binary);
}

function base64Decode(text) {
    const binary = atob(text || '');
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
}

function stripAnsi(text) {
    // 简易终端：去掉常见 ANSI 控制序列，保留可读文本。
    return text.replace(/\x1b\[[0-9;?]*[ -/]*[@-~]/g, '').replace(/\x1b\][^\x07]*\x07/g, '');
}

/**
 * 创建终端实例。返回对象由 .NET 通过 IJSObjectReference 调用：connect/disconnect/setFontSize/focus/dispose/clear/isFallback。
 * @param {HTMLElement} element 终端宿主元素
 * @param {any} dotNetRef DotNetObjectReference（OnTerminalEvent 回调）
 * @param {{fontSize?:number}} options
 */
export async function createTerminal(element, dotNetRef, options) {
    const opts = options || {};
    const host = element;
    host.classList.add('pf-terminal-host');

    let mode = 'xterm';
    let term = null;
    let fitAddon = null;
    let simpleOutput = null;
    let simpleInput = null;
    let socket = null;
    let heartbeatTimer = null;
    let disposer = null;
    let disposed = false;
    let fontSize = Math.max(10, Math.min(24, opts.fontSize || 14));
    let lastCols = 0;
    let lastRows = 0;
    let lastPingAt = 0;

    function notify(payload) {
        try {
            dotNetRef.invokeMethodAsync('OnTerminalEvent', payload);
        } catch {
            // 页面已销毁时忽略。
        }
    }

    function createSimpleTerminal() {
        host.innerHTML = '';
        host.classList.add('pf-terminal-simple');
        const wrap = document.createElement('div');
        wrap.className = 'pf-simple-terminal';
        simpleOutput = document.createElement('pre');
        simpleOutput.className = 'pf-simple-output';
        simpleInput = document.createElement('input');
        simpleInput.className = 'pf-simple-input';
        simpleInput.type = 'text';
        simpleInput.autocomplete = 'off';
        simpleInput.spellcheck = false;
        simpleInput.placeholder = '简易模式：输入命令后回车';
        simpleInput.addEventListener('keydown', (event) => {
            if (event.key === 'Enter') {
                event.preventDefault();
                const value = simpleInput.value;
                simpleInput.value = '';
                if (value.length > 0) {
                    sendInput(value + '\n');
                    appendSimple(value + '\n');
                } else {
                    sendInput('\n');
                }
            }
        });
        wrap.appendChild(simpleOutput);
        wrap.appendChild(simpleInput);
        host.appendChild(wrap);
        simpleInput.focus();
    }

    function appendSimple(bytesOrText) {
        if (!simpleOutput) {
            return;
        }
        let text = typeof bytesOrText === 'string'
            ? bytesOrText
            : new TextDecoder('utf-8', { fatal: false }).decode(bytesOrText);
        text = stripAnsi(text);
        simpleOutput.textContent += text;
        host.scrollTop = host.scrollHeight;
    }

    function write(bytes) {
        if (mode === 'xterm' && term) {
            term.write(bytes);
        } else {
            appendSimple(bytes);
        }
    }

    function showNotice(message, isError) {
        const text = (isError ? '\r\n\x1b[31m' : '\r\n\x1b[33m') + message + '\x1b[m\r\n';
        if (mode === 'xterm' && term) {
            term.write(text);
        } else {
            appendSimple('\n' + message + '\n');
        }
    }

    function sendInput(text) {
        if (!socket || socket.readyState !== WebSocket.OPEN) {
            return;
        }
        const bytes = new TextEncoder().encode(text);
        socket.send(JSON.stringify({ type: 'input', data: base64Encode(bytes) }));
    }

    function sendResize(cols, rows) {
        if (!socket || socket.readyState !== WebSocket.OPEN) {
            return;
        }
        if (cols <= 0 || rows <= 0) {
            return;
        }
        if (cols === lastCols && rows === lastRows) {
            return;
        }
        lastCols = cols;
        lastRows = rows;
        socket.send(JSON.stringify({ type: 'resize', cols, rows }));
    }

    function fitNow() {
        if (mode !== 'xterm' || !term) {
            return;
        }
        if (fitAddon) {
            try {
                fitAddon.fit();
            } catch {
                // 容器不可见时忽略。
            }
        }
        sendResize(term.cols, term.rows);
    }

    function scheduleFit() {
        if (disposer) {
            clearTimeout(disposer);
        }
        disposer = setTimeout(() => {
            disposer = null;
            fitNow();
        }, 120);
    }

    function stopHeartbeat() {
        if (heartbeatTimer) {
            clearInterval(heartbeatTimer);
            heartbeatTimer = null;
        }
    }

    function startHeartbeat() {
        stopHeartbeat();
        heartbeatTimer = setInterval(() => {
            if (socket && socket.readyState === WebSocket.OPEN) {
                lastPingAt = Date.now();
                socket.send(JSON.stringify({ type: 'ping', timestamp: lastPingAt }));
            }
        }, 15000);
    }

    function handleMessage(event) {
        let message;
        try {
            message = JSON.parse(event.data);
        } catch {
            return;
        }
        if (!message || typeof message.type !== 'string') {
            return;
        }
        switch (message.type) {
            case 'ready':
                notify({
                    kind: 'ready',
                    mode: message.mode,
                    nodeName: message.nodeName,
                    container: message.container,
                });
                break;
            case 'output':
                write(base64Decode(message.data));
                break;
            case 'pong':
                notify({ kind: 'latency', ms: Math.max(0, Date.now() - Number(message.timestamp || 0)) });
                break;
            case 'error':
                showNotice(message.message || '终端错误', true);
                notify({ kind: 'error', code: message.code, message: message.message });
                break;
            case 'closed':
                showNotice(message.message || '终端已断开', true);
                notify({
                    kind: 'closed',
                    reason: message.reason,
                    message: message.message,
                    canReconnect: !!message.canReconnect,
                });
                break;
            default:
                break;
        }
    }

    function connect(url) {
        if (disposed) {
            return;
        }
        if (socket) {
            try {
                socket.close(1000, 'reconnect');
            } catch {
                // ignore
            }
            socket = null;
        }
        lastCols = 0;
        lastRows = 0;
        notify({ kind: 'status', status: 'connecting' });
        const ws = new WebSocket(url);
        ws.binaryType = 'arraybuffer';
        socket = ws;

        ws.onopen = () => {
            notify({ kind: 'status', status: 'connected' });
            fitNow();
            if (mode === 'xterm' && term) {
                term.focus();
            } else if (simpleInput) {
                simpleInput.focus();
            }
            startHeartbeat();
        };
        ws.onmessage = handleMessage;
        ws.onerror = () => {
            notify({ kind: 'status', status: 'error', message: '无法建立终端连接' });
        };
        ws.onclose = (event) => {
            stopHeartbeat();
            if (socket === ws) {
                socket = null;
            }
            notify({ kind: 'status', status: 'closed', clean: event.wasClean });
        };
    }

    function disconnect() {
        stopHeartbeat();
        if (socket) {
            try {
                socket.close(1000, 'user-close');
            } catch {
                // ignore
            }
            socket = null;
        }
    }

    try {
        const xterm = await loadXterm();
        term = new xterm.Terminal({
            convertEol: false,
            cursorBlink: true,
            fontSize,
            fontFamily: '"Cascadia Mono", "Consolas", "Courier New", monospace',
            scrollback: 5000,
            theme: {
                background: '#111827',
                foreground: '#e5e7eb',
                cursor: '#93c5fd',
                selectionBackground: '#374151',
            },
        });
        term.open(host);
        if (xterm.FitAddon) {
            fitAddon = new xterm.FitAddon();
            term.loadAddon(fitAddon);
        }
        term.onData(sendInput);
        term.onResize((event) => sendResize(event.cols, event.rows));
        fitNow();
        notify({ kind: 'mode', mode: 'xterm' });
    } catch (error) {
        mode = 'simple';
        createSimpleTerminal();
        notify({ kind: 'mode', mode: 'simple', message: String(error && error.message ? error.message : error) });
    }

    window.addEventListener('resize', scheduleFit);

    return {
        connect: (url) => connect(url),
        disconnect: () => disconnect(),
        setFontSize: (value) => {
            fontSize = Math.max(10, Math.min(24, Number(value) || 14));
            if (term) {
                term.options.fontSize = fontSize;
            }
            fitNow();
            if (!term && !simpleOutput) {
                host.style.fontSize = fontSize + 'px';
            }
        },
        focus: () => {
            if (term) {
                term.focus();
            } else if (simpleInput) {
                simpleInput.focus();
            }
        },
        clear: () => {
            if (term) {
                term.clear();
            } else if (simpleOutput) {
                simpleOutput.textContent = '';
            }
        },
        isFallback: () => mode === 'simple',
        dispose: () => {
            if (disposed) {
                return;
            }
            disposed = true;
            disconnect();
            window.removeEventListener('resize', scheduleFit);
            if (disposer) {
                clearTimeout(disposer);
                disposer = null;
            }
            try {
                if (term) {
                    term.dispose();
                    term = null;
                }
            } catch {
                // ignore
            }
            if (simpleInput) {
                simpleInput.onkeydown = null;
            }
            host.innerHTML = '';
        },
    };
}
