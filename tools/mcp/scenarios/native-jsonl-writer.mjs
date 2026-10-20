import { appendFile } from 'node:fs/promises';

export function createNativeJsonlWriter(file) {
    let tail = Promise.resolve();
    return entry => {
        const line = JSON.stringify({ utc: new Date().toISOString(), ...entry }) + '\n';
        tail = tail.then(() => appendFile(file, line));
        return tail;
    };
}
