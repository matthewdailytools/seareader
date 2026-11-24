import { prepareModel } from './translation-models.mjs';
import { parentPort, workerData } from 'node:worker_threads';

const { pipeline, env } = await import('@huggingface/transformers');
const { cacheDir, route, install, text } = workerData;
env.allowRemoteModels = false;
env.allowLocalModels = true;
env.useFSCache = false;

let result = text;
for (const model of route) {
  env.localModelPath = await prepareModel(cacheDir, model, install);
  const translate = await pipeline('translation', model.name, {
    revision: model.revision,
    dtype: 'q8',
    device: 'cpu',
    local_files_only: true
  });
  try {
    if (!install) {
      const segments = result.split(/(?<=[。！？.!?])\s*|\n+/u).flatMap(segment =>
        segment.trim().match(/[\s\S]{1,220}/gu) ?? []
      ).filter(segment => segment.trim());
      const translated = [];
      for (const segment of segments) {
        const output = await translate(segment, { ...model.options, max_new_tokens: 300 });
        translated.push(output[0].translation_text);
      }
      result = translated.join(' ');
    }
  } finally {
    await translate.dispose();
  }
}
parentPort.postMessage(result ?? '');
