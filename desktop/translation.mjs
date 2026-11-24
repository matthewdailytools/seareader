import { Worker } from 'node:worker_threads';

const models = {
  'en:zh': { name: 'Xenova/opus-mt-en-zh', revision: '046f55aec303cdee3e0318604406d4df20f1e8ea' },
  'zh:en': { name: 'Xenova/opus-mt-zh-en', revision: '39d480d52a9ea3065a1f117adfe4dbc55de10e6f' },
  'en:ja': {
    name: 'Xenova/m2m100_418M', revision: '9c374f0b7aca709787cea97b047bfbbd1559d177',
    options: { src_lang: 'en', tgt_lang: 'ja' }
  },
  'ja:en': { name: 'Xenova/opus-mt-ja-en', revision: '1a906cfaaf7c8f4193f67f5885c082aa6dbd9d16' }
};
const languages = new Set(['zh', 'en', 'ja']);

export function registerTranslation(register, cacheDir) {
  let active;
  register('translation:run', (id, source, target, text, install) => {
    if (active) throw new Error('请等待当前翻译任务结束');
    if (typeof id !== 'string' || !/^[a-f0-9-]{36}$/.test(id) ||
        !languages.has(source) || !languages.has(target) || source === target ||
        typeof install !== 'boolean' || typeof text !== 'string' || text.length > 6000 ||
        (!install && !text.trim())) throw new Error('无效的翻译请求');
    const direct = models[source + ':' + target];
    const route = direct ? [direct] : [models[source + ':en'], models['en:' + target]];
    const worker = new Worker(new URL('./translation-worker.mjs', import.meta.url), {
      execArgv: [],
      workerData: { cacheDir, route, install, text: install ? '' : text }
    });
    active = { id, worker };
    return new Promise((resolve, reject) => {
      let result;
      worker.once('message', value => { result = value; });
      worker.once('error', error => {
        const reason = error.message;
        if (!install && /local.file|cache|remote models/i.test(reason)) {
          reject(new Error('该语言方向的离线模型尚未安装'));
        } else {
          reject(new Error(install ? '无法安装翻译模型，请检查网络和磁盘空间' : '离线翻译失败'));
        }
      });
      worker.once('exit', code => {
        if (active?.worker === worker) active = undefined;
        if (code === 0 && typeof result === 'string') resolve(result);
        else reject(new Error('翻译任务已结束'));
      });
    });
  });
  register('translation:cancel', async id => {
    if (active?.id === id) await active.worker.terminate();
  });
}
