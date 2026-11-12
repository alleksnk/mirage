const net = require('net');
const tls = require('tls');

function test(host, proxyPort) {
    const s = net.connect(proxyPort, '127.0.0.1', () => {
        s.write(Buffer.from([5, 1, 0]));
    });
    s.once('data', () => {
        const hBuf = Buffer.from(host);
        s.write(Buffer.concat([Buffer.from([5, 1, 0, 3, hBuf.length]), hBuf, Buffer.from([1, 187])]));
        s.once('data', (d2) => {
            if (d2[1] !== 0) { s.destroy(); return; }
            const ssl = tls.connect({ socket: s, servername: host }, () => {
                console.log(host + ' ---> SUCCESS! TLS CONNECTED!');
                ssl.destroy();
            });
            ssl.on('error', (e) => console.log(host + ' FAIL: ' + e.message.split('\n')[0]));
        });
    });
}

test('rutor.info', 1085);
test('www.youtube.com', 1085);
