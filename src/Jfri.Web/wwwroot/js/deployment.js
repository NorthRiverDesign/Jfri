// Streams a deployment's log by polling the JSON log endpoint (no inline script: the CSP forbids it).
(() => {
	const log = document.getElementById("log");
	if (!log) {
		return;
	}
	const id = log.dataset.deployment;
	const status = document.getElementById("status");
	const result = document.getElementById("result");
	let offset = 0;
	let first = true;

	async function poll() {
		let data;
		try {
			const response = await fetch(`/api/deployments/${id}/log?offset=${offset}`, { credentials: "same-origin" });
			if (!response.ok) {
				throw new Error(`HTTP ${response.status}`);
			}
			data = await response.json();
		} catch {
			setTimeout(poll, 5000);
			return;
		}
		if (first) {
			log.textContent = "";
			first = false;
		}
		if (data.text) {
			const atBottom = window.innerHeight + window.scrollY >= document.body.offsetHeight - 40;
			log.textContent += data.text;
			if (atBottom) {
				window.scrollTo(0, document.body.scrollHeight);
			}
		}
		offset = data.offset;
		status.textContent = data.status;
		status.className = `badge ${data.status}`;
		if (data.status === "succeeded" || data.status === "failed") {
			result.textContent = "";
			if (data.url) {
				const a = document.createElement("a");
				a.href = data.url;
				a.textContent = data.url;
				a.className = "live";
				a.target = "_blank";
				a.rel = "noopener noreferrer";
				result.appendChild(a);
			}
			if (data.error) {
				const span = document.createElement("span");
				span.className = "error";
				span.textContent = data.error;
				result.appendChild(span);
			}
			if (log.textContent === "") {
				log.textContent = "(no log output)";
			}
			return;
		}
		setTimeout(poll, 1500);
	}

	poll();
})();
