(() => {
  'use strict';

  const FMIN = 20;
  const FMAX = 20000;
  const PAD_L = 62;
  const PAD_R = 24;
  const canvas = document.querySelector('#visualizer');
  if (!canvas) return;

  const context = canvas.getContext('2d');
  let analyser = null;
  let animationFrame = null;

  function resize() {
    const rect = canvas.getBoundingClientRect();
    const dpr = window.devicePixelRatio || 1;

    canvas.width = Math.round(rect.width * dpr);
    canvas.height = Math.round(rect.height * dpr);
    context.setTransform(dpr, 0, 0, dpr, 0, 0);
  }

  // Linear interpolation function; could be used for creating gradient with HSL:
  function lerp(index, rangeIn, rangeOut) {
    return (index - rangeIn[0]) * (rangeOut[1] - rangeOut[0]) / (rangeIn[1] - rangeIn[0] + rangeOut[0]);
  }

  function draw() {
    const rect = canvas.getBoundingClientRect();
    const width = rect.width;
    const height = rect.height;

    context.clearRect(0, 0, width, height);
    context.fillStyle = getComputedStyle(document.documentElement).getPropertyValue('--bg').trim() || '#EEF1F4';
    context.fillRect(0, 0, width, height);

    // The audio context is created only after the participant starts a session.
    // Look it up on every frame so the visualiser starts automatically at that point.
    analyser = window.eqTrainerVisuals?.getAnalyser() || analyser;
    if (analyser) {
      const values = new Uint8Array(analyser.frequencyBinCount);
      analyser.getByteFrequencyData(values);
      const plotWidth = width - PAD_L - PAD_R;
      const binHz = analyser.context.sampleRate / analyser.fftSize;
      const logRange = Math.log(FMAX / FMIN);

      // Trying a different approach to gradient:
      // adding colours manually by their name
      const gradient = context.createLinearGradient(PAD_L, 0, width - PAD_R, 0);
      gradient.addColorStop(0,  'crimson');
      gradient.addColorStop(0.1,'red');
      gradient.addColorStop(0.2,'orange');
      gradient.addColorStop(0.3,'gold');
      gradient.addColorStop(0.4,'yellow');
      gradient.addColorStop(0.5,'greenYellow');
      gradient.addColorStop(0.6,'springGreen');
      gradient.addColorStop(0.7,'turquoise');
      gradient.addColorStop(0.8,'deepSkyBlue');
      gradient.addColorStop(0.9,'dodgerBlue');
      gradient.addColorStop(1,  'midnightBlue');

/*      //changed version with HSL:
      const gradient = context.createLinearGradient(PAD_L, 0, width - PAD_R, 0);
      gradient.addColorStop(0, 'hsl(0, 100%, 50%)');
      gradient.addColorStop(0.5, 'hsl(120, 100%, 50%)');
      gradient.addColorStop(1, 'hsl(240, 100%, 50%)');*/

/*      //original version:
      const gradient = context.createLinearGradient(PAD_L, 0, width - PAD_R, 0);
      gradient.addColorStop(0, 'rgba(43, 71, 201, 0.55)');
      gradient.addColorStop(0.5, 'rgba(36, 128, 79, 0.42)');
      gradient.addColorStop(1, 'rgba(184, 121, 8, 0.48)');*/

      context.fillStyle = gradient;
      context.beginPath();
      context.moveTo(PAD_L, height);

      let started = false;
      for (let i = 0; i < values.length; i++) {
        const frequency = i * binHz;
        if (frequency < FMIN) continue;
        if (frequency > FMAX) break;
        const x = PAD_L + Math.log(frequency / FMIN) / logRange * plotWidth;
        const level = values[i] / 255;
        const y = height - level * height * 0.8;
        if (!started) {
          context.lineTo(x, height);
          started = true;
        }
        context.lineTo(x, y);
      }

      context.lineTo(width - PAD_R, height);
      context.closePath();
      context.fill();
    } else {
      context.fillStyle = getComputedStyle(document.documentElement).getPropertyValue('--muted').trim() || '#5D6878';
      context.font = '12px "Instrument Sans", system-ui, sans-serif';
      context.fillText('Start a trial and play a sound to see the spectrum', PAD_L, height - 8);
    }

    animationFrame = requestAnimationFrame(draw);
  }

  function connect() {
    analyser = window.eqTrainerVisuals?.getAnalyser() || null;
    resize();
    draw();
  }

  window.addEventListener('resize', resize);
  const panel = canvas.parentElement;
  if (panel && 'ResizeObserver' in window) {
    new ResizeObserver(resize).observe(panel);
  }

  if (document.fonts) {
    document.fonts.ready.then(connect);
  } else {
    connect();
  }
})();