//! Coalesce refreshes and bound both queued work and native query threads.
use crate::a2s::{self, Query};
use std::collections::{HashSet, VecDeque};
use std::sync::mpsc::{channel, Receiver, Sender};
use std::time::Duration;

pub struct Jobs {
    queue: VecDeque<String>,
    pending: HashSet<String>,
    active: usize,
    tx: Sender<(String, Result<Query, String>)>,
    rx: Receiver<(String, Result<Query, String>)>,
}
impl Jobs {
    pub fn new() -> Self {
        let (tx, rx) = channel();
        Self {
            queue: VecDeque::new(),
            pending: HashSet::new(),
            active: 0,
            tx,
            rx,
        }
    }
    pub fn enqueue(&mut self, address: &str) -> Result<(), String> {
        if self.pending.contains(address) {
            return Ok(());
        }
        if self.pending.len() >= 1024 {
            return Err("Too many pending servers; try again after refresh completes".into());
        }
        self.pending.insert(address.into());
        self.queue.push_back(address.into());
        Ok(())
    }
    pub fn pump(&mut self) -> Vec<(String, Result<Query, String>)> {
        let mut results = Vec::new();
        while let Ok((address, result)) = self.rx.try_recv() {
            self.active -= 1;
            self.pending.remove(&address);
            results.push((address, result));
        }
        while self.active < 4 {
            let Some(address) = self.queue.pop_front() else {
                break;
            };
            self.active += 1;
            let tx = self.tx.clone();
            std::thread::spawn(move || {
                let result = a2s::query(&address, 27026, Duration::from_millis(2500), true);
                let _ = tx.send((address, result));
            });
        }
        results
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn refreshes_coalesce_and_work_is_bounded() {
        let mut jobs = Jobs::new();
        for _ in 0..1000 {
            jobs.enqueue("127.0.0.1:1").unwrap();
        }
        assert_eq!(jobs.pending.len(), 1);
        for n in 2..1025 {
            jobs.enqueue(&format!("127.0.0.1:{n}")).unwrap();
        }
        assert!(jobs.enqueue("overflow:1").is_err());
        jobs.pump();
        assert_eq!(jobs.active, 4);
        assert_eq!(jobs.queue.len(), 1020);
    }
}
