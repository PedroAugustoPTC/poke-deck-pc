import { mount } from 'svelte';
// As fontes vão embutidas no exe (só o latin, que cobre o português): o app não depende do Google Fonts.
import '@fontsource/barlow/latin-400.css';
import '@fontsource/barlow/latin-500.css';
import '@fontsource/barlow/latin-600.css';
import '@fontsource/barlow/latin-700.css';
import '@fontsource/barlow-semi-condensed/latin-500.css';
import '@fontsource/barlow-semi-condensed/latin-600.css';
import '@fontsource/pixelify-sans/latin-600.css';
import './app.css';
import App from './App.svelte';

export default mount(App, { target: document.getElementById('app') });
